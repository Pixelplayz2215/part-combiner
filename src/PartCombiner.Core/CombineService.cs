using System.Diagnostics;

namespace PartCombiner.Core;

public sealed class CombineOptions
{
    /// <summary>Where results go. Null or empty means "next to the parts".</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>For byte-split archives (.7z.001 …): unpack the contents (true) or just rebuild the archive file (false).</summary>
    public bool ExtractArchives { get; init; } = true;

    public string? Password { get; init; }

    /// <summary>Move the parts to the Recycle Bin after a successful combine.</summary>
    public bool RecycleParts { get; init; }
}

public enum CombineStage { Joining, Extracting, Unpacking, Finishing }

public readonly record struct CombineProgress(CombineStage Stage, double Fraction, string? CurrentItem);

public sealed record CombineResult(
    bool Success,
    string? OutputPath,
    string? Error,
    bool PasswordProblem = false,
    string? Warning = null,
    bool ChecksumVerified = false);

/// <summary>Turns a <see cref="PartSet"/> back into a folder or file.</summary>
public sealed class CombineService(SevenZipEngine? sevenZip)
{
    public SevenZipEngine? SevenZip { get; } = sevenZip;

    /// <summary>Free bytes for a folder's drive, or null if unknown. Replaceable so tests can simulate a full disk.</summary>
    internal Func<string, (string Drive, long Free)?> FreeSpace { get; init; } = DriveFreeSpace;

    /// <summary>Whether the set will be unpacked (true) or byte-joined (false) with the given setting.</summary>
    public static bool WillExtract(PartSet set, bool extractArchives) =>
        set.MustExtract || (set.Kind == SetKind.SplitArchive && extractArchives);

    public async Task<CombineResult> CombineAsync(
        PartSet set,
        CombineOptions options,
        IProgress<CombineProgress>? progress,
        CancellationToken ct)
    {
        var blocker = set.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error);
        if (blocker is not null)
            return Fail(blocker.Message);

        foreach (var part in set.Parts)
        {
            var info = new FileInfo(part.Path);
            if (!info.Exists)
                return Fail($"{part.Name} no longer exists");
            if (info.Length != part.Size)
                return Fail($"{part.Name} changed since it was added — add the parts again");
        }

        var dest = string.IsNullOrWhiteSpace(options.OutputDirectory) ? set.Directory : options.OutputDirectory!;
        try
        {
            Directory.CreateDirectory(dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail("Can't use the output folder: " + ex.Message);
        }

        var result = WillExtract(set, options.ExtractArchives)
            ? await ExtractAsync(set, dest, options.Password, progress, ct).ConfigureAwait(false)
            : await JoinAsync(set, dest, progress, ct).ConfigureAwait(false);

        if (result.Success && options.RecycleParts)
        {
            var files = set.Parts.Select(p => p.Path).ToList();
            if (set.Crc is not null)
                files.Add(set.Crc.SourceFile);
            if (!RecycleBin.Send(files))
                result = result with { Warning = Join(result.Warning, "Some parts couldn't be moved to the Recycle Bin") };
        }

        return result;
    }

    private async Task<CombineResult> JoinAsync(PartSet set, string dest, IProgress<CombineProgress>? progress, CancellationToken ct)
    {
        if (CheckFreeSpace(dest, set.TotalSize) is string spaceError)
            return Fail(spaceError);

        var temp = Path.Combine(dest, PartDetector.TempPrefix + NewId() + ".tmp");
        double total = Math.Max(1, set.TotalSize);
        var bytes = new ThrottledProgress<long>(b => progress?.Report(new(CombineStage.Joining, b / total, null)));
        uint? expectedCrc = set.Crc?.Crc32;

        try
        {
            var crc = await FileJoiner.JoinAsync(set.Parts.Select(p => p.Path).ToList(), temp, expectedCrc.HasValue, bytes, ct)
                .ConfigureAwait(false);

            var target = UniquePath(dest, set.JoinedFileName, isFile: true);
            File.Move(temp, target);
            progress?.Report(new(CombineStage.Finishing, 1, null));

            if (expectedCrc.HasValue && crc != expectedCrc)
                return new CombineResult(false, target, "Checksum mismatch — the joined file doesn't match the .crc file");

            return new CombineResult(true, target, null, ChecksumVerified: expectedCrc.HasValue);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(temp);
            return Fail(ex.Message);
        }
    }

    private async Task<CombineResult> ExtractAsync(
        PartSet set,
        string dest,
        string? password,
        IProgress<CombineProgress>? progress,
        CancellationToken ct)
    {
        if (SevenZip is null)
        {
            return Fail(set.MustExtract
                ? "7-Zip is needed to extract this archive"
                : "7-Zip is needed to extract — or choose \"Keep as archive\"");
        }

        // 7-Zip opens "name.001" split sets itself. Other namings (.part1, .aa, .000) are joined to a temp archive first.
        bool needsJoin = set.Kind == SetKind.SplitArchive && !(set.Scheme == NamingScheme.Numbered && set.FirstIndex == 1);
        bool twoStepTar = FormatInfo.IsCompressedTar(set.Format);

        int phases = 1 + (needsJoin ? 1 : 0) + (twoStepTar ? 1 : 0);
        double weight = 1.0 / phases;
        int phase = 0;
        void Report(CombineStage stage, double fraction, string? item) =>
            progress?.Report(new(stage, Math.Clamp((phase + fraction) * weight, 0, 1), item));

        string id = NewId();
        string work = Path.Combine(dest, PartDetector.TempPrefix + id);
        string? joined = null;

        try
        {
            if (needsJoin)
            {
                if (CheckFreeSpace(dest, set.TotalSize) is string spaceError)
                    return Fail(spaceError);

                joined = Path.Combine(dest, PartDetector.TempPrefix + id + FormatInfo.Extension(set.Format));
                double total = Math.Max(1, set.TotalSize);
                await FileJoiner.JoinAsync(
                        set.Parts.Select(p => p.Path).ToList(), joined, false,
                        new ThrottledProgress<long>(b => Report(CombineStage.Joining, b / total, null)), ct)
                    .ConfigureAwait(false);
                phase++;
            }

            string source = joined ?? ExtractSource(set);
            if (await CheckRoomToExtractAsync(source, dest, password, ct).ConfigureAwait(false) is string roomError)
                return Fail(roomError);

            CreateHiddenDirectory(work);
            var result = await SevenZip.ExtractAsync(
                    source, work, password,
                    new ThrottledProgress<ExtractProgress>(e => Report(CombineStage.Extracting, e.Percent / 100.0, e.CurrentFile)), ct)
                .ConfigureAwait(false);
            if (!result.Success)
                return new CombineResult(false, null, result.Error + NothingSaved, result.PasswordProblem);
            phase++;

            string? warning = result.Warning;

            if (joined is not null)
            {
                TryDeleteFile(joined);
                joined = null;
            }

            // .tar.gz and friends: 7-Zip unpacks one layer at a time, so unpack the inner .tar too.
            if (twoStepTar && SingleEntry(work) is FileInfo tar && tar.Extension.Equals(".tar", StringComparison.OrdinalIgnoreCase))
            {
                // The .tar stays on disk while its contents are unpacked next to it.
                if (await CheckRoomToExtractAsync(tar.FullName, dest, null, ct).ConfigureAwait(false) is string tarRoomError)
                    return Fail(tarRoomError);

                string inner = Path.Combine(dest, PartDetector.TempPrefix + NewId());
                CreateHiddenDirectory(inner);
                var tarResult = await SevenZip.ExtractAsync(
                        tar.FullName, inner, null,
                        new ThrottledProgress<ExtractProgress>(e => Report(CombineStage.Unpacking, e.Percent / 100.0, e.CurrentFile)), ct)
                    .ConfigureAwait(false);
                TryDeleteDirectory(work);
                work = inner;
                if (!tarResult.Success)
                    return new CombineResult(false, null, tarResult.Error + NothingSaved);
                warning = Join(warning, tarResult.Warning);
            }

            progress?.Report(new(CombineStage.Finishing, 1, null));
            var output = PlaceOutput(work, dest, set.ExtractFolderName, ref warning);
            return new CombineResult(true, output, null, Warning: warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            if (joined is not null)
                TryDeleteFile(joined);
            TryDeleteDirectory(work);
        }
    }

    /// <summary>The file 7-Zip should be pointed at.</summary>
    internal static string ExtractSource(PartSet set) =>
        set.Kind == SetKind.ZipSpanned ? set.Parts[^1].Path : set.FirstPartPath;

    /// <summary>
    /// Moves extracted content from the temp folder into place. An archive with a single top-level item
    /// (typically the original folder) is placed as-is; several items get a folder named after the archive.
    /// </summary>
    private static string PlaceOutput(string work, string dest, string folderName, ref string? warning)
    {
        var entries = new DirectoryInfo(work).GetFileSystemInfos();
        if (entries.Length == 0)
        {
            warning = Join(warning, "The archive was empty");
            return dest;
        }

        if (entries.Length == 1)
        {
            var entry = entries[0];
            bool isFile = entry is FileInfo;
            var target = UniquePath(dest, entry.Name, isFile);
            if (entry is DirectoryInfo dir)
                dir.MoveTo(target);
            else
                ((FileInfo)entry).MoveTo(target);
            return target;
        }

        var folder = UniquePath(dest, folderName, isFile: false);
        Directory.Move(work, folder);
        var attributes = File.GetAttributes(folder);
        File.SetAttributes(folder, attributes & ~FileAttributes.Hidden);
        return folder;
    }

    private static FileSystemInfo? SingleEntry(string dir)
    {
        var entries = new DirectoryInfo(dir).GetFileSystemInfos();
        return entries.Length == 1 ? entries[0] : null;
    }

    /// <summary>"name.ext" → "name (2).ext" … until the path is free.</summary>
    internal static string UniquePath(string dir, string name, bool isFile)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        string stem = name, ext = "";
        if (isFile)
        {
            ext = Path.GetExtension(name);
            stem = Path.GetFileNameWithoutExtension(name);
            if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                ext = ".tar" + ext;
                stem = stem[..^4];
            }
        }

        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Asks 7-Zip how big the archive is once unpacked and fails early if that won't fit, instead of
    /// 7-Zip running out of space halfway through. Skipped when the size can't be read (e.g. encrypted headers).
    /// </summary>
    private async Task<string?> CheckRoomToExtractAsync(string archive, string dest, string? password, CancellationToken ct)
    {
        if (SevenZip is null || FreeSpace(dest) is null)
            return null;
        long? unpacked = await SevenZip.GetUnpackedSizeAsync(archive, password, ct).ConfigureAwait(false);
        return unpacked is long size ? CheckFreeSpace(dest, size) : null;
    }

    private string? CheckFreeSpace(string dest, long needed)
    {
        if (FreeSpace(dest) is not var (drive, free) || free >= needed)
            return null;
        return $"Not enough free space on {drive} (needs {Bytes.Format(needed)}, {Bytes.Format(free)} free)";
    }

    private static (string Drive, long Free)? DriveFreeSpace(string dest)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dest));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.Name.TrimEnd('\\'), drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CreateHiddenDirectory(string path)
    {
        var info = Directory.CreateDirectory(path);
        info.Attributes |= FileAttributes.Hidden;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        // Only ever called on our own ".pc-" temp folders.
        if (!Path.GetFileName(path).StartsWith(PartDetector.TempPrefix, StringComparison.Ordinal))
            return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150); // 7-Zip may still be releasing handles right after being stopped
            }
        }
    }

    /// <summary>Failed extractions happen in a temp folder that is thrown away, so nothing partial is left behind.</summary>
    private const string NothingSaved = " — nothing was saved";

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];

    private static CombineResult Fail(string error) => new(false, null, error);

    private static string? Join(string? a, string? b) =>
        string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " · " + b;

    /// <summary>Forwards at most ~20 updates per second so the UI isn't flooded.</summary>
    private sealed class ThrottledProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastTicks = -1;

        public void Report(T value)
        {
            long now = _clock.ElapsedMilliseconds;
            if (_lastTicks >= 0 && now - _lastTicks < 50) return;
            _lastTicks = now;
            report(value);
        }
    }
}
