using System.Globalization;
using System.IO.Hashing;

namespace PartCombiner.Core;

/// <summary>How the parts are named (and, for the archive styles, packed first).</summary>
public enum SplitStyle
{
    /// <summary>movie.mkv.001, movie.mkv.002 …</summary>
    Numbered,
    /// <summary>movie.mkv.part1, movie.mkv.part2 … (zero-padded to the part count: part01 … part12)</summary>
    PartSuffix,
    /// <summary>movie.mkv.aa, movie.mkv.ab … (Unix split)</summary>
    Alphabetic,
    /// <summary>Packed into movie.7z first, then cut into movie.7z.001 … (7-Zip opens these directly)</summary>
    SevenZip,
    /// <summary>Packed into movie.zip first, then cut into movie.zip.001 …</summary>
    Zip,
}

public sealed record SplitOptions
{
    public required string Source { get; init; }
    public required int Parts { get; init; }
    public SplitStyle Style { get; init; } = SplitStyle.Numbered;
    /// <summary>Where the parts go. Null or empty means next to the source.</summary>
    public string? OutputDirectory { get; init; }
    /// <summary>Also write a Total Commander style .crc file (plain splits only), so joining can be verified.</summary>
    public bool WriteCrc { get; init; }
    /// <summary>Encrypts the parts (AES-256). Only the .7z and .zip styles can carry a password.</summary>
    public string? Password { get; init; }
}

public enum SplitStage { Packing, Splitting, Checking }

public readonly record struct SplitProgress(SplitStage Stage, double Fraction);

public sealed record SplitResult(bool Success, IReadOnlyList<string> Parts, string? Error, long PartSize = 0, bool Verified = false)
{
    public string? FirstPart => Parts.Count > 0 ? Parts[0] : null;
}

/// <summary>Cuts a file (or, packed as .7z/.zip, a folder) into a chosen number of equal parts.</summary>
public static class FileSplitter
{
    public const int MaxParts = 99_999;
    public const int MaxLetterParts = 26 * 26;
    private const int BufferSize = 4 * 1024 * 1024;

    public static bool IsArchiveStyle(SplitStyle style) => style is SplitStyle.SevenZip or SplitStyle.Zip;

    /// <summary>The name parts are based on: "movie.mkv" for plain splits, "movie.7z" / "Photos.zip" for archives.</summary>
    public static string BaseName(string source, SplitStyle style)
    {
        var name = Path.GetFileName(Path.GetFullPath(source).TrimEnd('\\'));
        return style switch
        {
            SplitStyle.SevenZip => (Directory.Exists(source) ? name : Path.GetFileNameWithoutExtension(name)) + ".7z",
            SplitStyle.Zip => (Directory.Exists(source) ? name : Path.GetFileNameWithoutExtension(name)) + ".zip",
            _ => name,
        };
    }

    /// <summary>File names for <paramref name="count"/> parts, in order.</summary>
    public static IReadOnlyList<string> PartNames(string baseName, SplitStyle style, int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = PartName(baseName, style, i, count);
        return names;
    }

    /// <summary>The name of part <paramref name="index"/> (0-based) in a set of <paramref name="count"/>.</summary>
    public static string PartName(string baseName, SplitStyle style, int index, int count)
    {
        int digits = count.ToString(CultureInfo.InvariantCulture).Length;
        var n = (index + 1).ToString(CultureInfo.InvariantCulture);
        return style switch
        {
            SplitStyle.PartSuffix => $"{baseName}.part{n.PadLeft(digits, '0')}",
            SplitStyle.Alphabetic => $"{baseName}.{Letters(index, 2)}", // two letters, see MaxLetterParts
            _ => $"{baseName}.{n.PadLeft(Math.Max(3, digits), '0')}",
        };
    }

    /// <summary>
    /// Size of every part but the last. Parts must be equal (the last may be smaller), so a tiny file can't always
    /// be cut into exactly the number asked for — <see cref="ActualCount"/> says how many it really makes.
    /// </summary>
    public static long PartSize(long total, int count) => Math.Max(1, (total + count - 1) / count);

    public static int ActualCount(long total, int count) =>
        total <= 0 ? 0 : (int)Math.Min(int.MaxValue, (total + PartSize(total, count) - 1) / PartSize(total, count));

    /// <summary>Why these settings can't work, or null if they can (the archive styles are only checked for the source).</summary>
    public static string? Validate(SplitOptions o, bool sevenZipAvailable)
    {
        bool isFolder = Directory.Exists(o.Source);
        if (!isFolder && !File.Exists(o.Source))
            return "Choose a file to split";
        if (isFolder && !IsArchiveStyle(o.Style))
            return "A folder can only be split as .7z or .zip — choose one of those styles";
        if (!string.IsNullOrEmpty(o.Password) && !IsArchiveStyle(o.Style))
            return "A password needs the 7z or Zip style — plain parts can't be encrypted";
        // Zip encryption only takes plain ASCII passwords (7-Zip refuses others); 7z takes any characters.
        if (o.Style == SplitStyle.Zip && o.Password is { } pw && pw.Any(c => c is < ' ' or > '~'))
            return "Zip passwords can't contain accents or special letters — use the 7z style for this password";
        if (IsArchiveStyle(o.Style) && !sevenZipAvailable)
            return "7-Zip is needed for .7z and .zip parts";
        if (o.Parts < 2)
            return "Choose at least 2 parts";
        if (o.Parts > MaxParts)
            return $"At most {MaxParts:N0} parts";
        // Two letters (aa … zz). Three would eventually spell names like ".rar" or ".zip" and confuse detection.
        if (o.Style == SplitStyle.Alphabetic && o.Parts > MaxLetterParts)
            return $"Letter names (.aa … .zz) go up to {MaxLetterParts} parts — choose another style for more";
        if (!isFolder)
        {
            long size = new FileInfo(o.Source).Length;
            if (size == 0)
                return "The file is empty";
            if (!IsArchiveStyle(o.Style) && ActualCount(size, o.Parts) != o.Parts)
                return $"This file is too small for {o.Parts} equal parts (it would make {ActualCount(size, o.Parts)})";
        }
        return null;
    }

    public static async Task<SplitResult> SplitAsync(
        SplitOptions o,
        SevenZipEngine? sevenZip,
        IProgress<SplitProgress>? progress,
        CancellationToken ct)
    {
        if (Validate(o, sevenZip is not null) is string invalid)
            return Fail(invalid);

        var source = Path.GetFullPath(o.Source).TrimEnd('\\');
        var dest = string.IsNullOrWhiteSpace(o.OutputDirectory) ? Path.GetDirectoryName(source)! : o.OutputDirectory!;
        try
        {
            Directory.CreateDirectory(dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail("Can't use the output folder: " + ex.Message);
        }

        var baseName = BaseName(source, o.Style);
        var names = PartNames(baseName, o.Style, o.Parts);
        var targets = names.Select(n => Path.Combine(dest, n)).ToList();
        string? crcPath = o.WriteCrc && !IsArchiveStyle(o.Style) ? Path.Combine(dest, baseName + ".crc") : null;

        // Never overwrite anything.
        var clash = targets.Append(crcPath).FirstOrDefault(p => p is not null && (File.Exists(p) || Directory.Exists(p)));
        if (clash is not null)
            return Fail($"{Path.GetFileName(clash)} already exists in that folder — move it or choose another folder");

        string? packed = null;
        var created = new List<string>();
        try
        {
            string input = source;
            bool archive = IsArchiveStyle(o.Style);
            if (archive)
            {
                packed = Path.Combine(dest, $"{PartDetector.TempPrefix}{Guid.NewGuid().ToString("N")[..10]}{Path.GetExtension(baseName)}");
                var packProgress = new Progress<ExtractProgress>(p => progress?.Report(new(SplitStage.Packing, p.Percent / 200.0)));
                var packResult = await sevenZip!.CreateArchiveAsync(source, packed,
                    o.Style == SplitStyle.SevenZip ? "7z" : "zip", o.Password, packProgress, ct).ConfigureAwait(false);
                if (!packResult.Success)
                    return Fail("Packing failed: " + packResult.Error);
                input = packed;
            }

            long total = new FileInfo(input).Length;
            if (ActualCount(total, o.Parts) != o.Parts)
                return Fail($"The packed archive is too small for {o.Parts} equal parts (it would make {ActualCount(total, o.Parts)})");
            if (FreeSpace(dest) is long free && free < total)
                return Fail($"Not enough free space (needs {Bytes.Format(total)}, {Bytes.Format(free)} free)");

            long partSize = PartSize(total, o.Parts);
            double offset = archive ? 0.5 : 0, scale = archive ? 0.5 : 1;
            var crc = crcPath is not null ? new Crc32() : null;
            var buffer = new byte[(int)Math.Min(BufferSize, partSize)];

            await using (var reader = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long done = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    long size = Math.Min(partSize, total - done);
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        BufferSize = 0,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        PreallocationSize = size,
                    };
                    await using var writer = new FileStream(targets[i], options);
                    created.Add(targets[i]);

                    long left = size;
                    while (left > 0)
                    {
                        int read = await reader.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                        if (read == 0) throw new IOException("The file got shorter while it was being split");
                        crc?.Append(buffer.AsSpan(0, read));
                        await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        left -= read;
                        done += read;
                        progress?.Report(new(SplitStage.Splitting, offset + scale * done / total));
                    }
                }
            }

            if (crcPath is not null)
            {
                await File.WriteAllTextAsync(crcPath,
                    $"filename={Path.GetFileName(source)}\r\nsize={total}\r\ncrc32={crc!.GetCurrentHashAsUInt32():X8}\r\n", ct)
                    .ConfigureAwait(false);
                created.Add(crcPath);
            }

            // Make sure Part Combiner itself recognizes the result as one complete set.
            progress?.Report(new(SplitStage.Checking, 1));
            var check = PartDetector.Detect([targets[0]], ct);
            bool verified = check.Sets.Count == 1 && !check.Sets[0].IsBlocked && check.Sets[0].Parts.Count == o.Parts
                            && check.Sets[0].TotalSize == total;

            created.Clear(); // keep them
            return new SplitResult(true, targets, null, partSize, verified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            foreach (var path in created) TryDelete(path); // only non-empty after a failure or cancel
            if (packed is not null) TryDelete(packed);
        }
    }

    private static long? FreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Letters(int index, int length)
    {
        var chars = new char[length];
        for (int i = length - 1; i >= 0; i--)
        {
            chars[i] = (char)('a' + index % 26);
            index /= 26;
        }
        return new string(chars);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static SplitResult Fail(string error) => new(false, [], error);
}
