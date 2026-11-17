using System.Globalization;

namespace PartCombiner.Core;

public sealed record DetectionResult(IReadOnlyList<PartSet> Sets, IReadOnlyList<string> UnrecognizedFiles);

/// <summary>
/// Finds sets of part files in dropped files and folders and checks them for completeness and consistency.
/// Anything that suggests parts are missing, damaged or don't belong together is an Error, and the set
/// will not be combined.
/// </summary>
public static class PartDetector
{
    /// <summary>Prefix used for the hidden temporary files and folders the combiner creates.</summary>
    public const string TempPrefix = ".pc-";

    /// <summary>How many header bytes are read from each part for consistency checks.</summary>
    private const int HeadBytes = 512;

    /// <summary>
    /// Detect part sets. Dropped folders are scanned (plus one level of subfolders); for dropped files the
    /// sibling parts in the same folder are found automatically.
    /// </summary>
    public static DetectionResult Detect(IEnumerable<string> inputs, CancellationToken ct = default)
    {
        // Folder → explicit files dropped from it (null = every set in the folder).
        var scopes = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            string full;
            try { full = Path.GetFullPath(input); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            if (Directory.Exists(full))
            {
                scopes[Path.TrimEndingDirectorySeparator(full)] = null;
                foreach (var sub in SafeSubdirectories(full))
                    scopes[sub] = null;
            }
            else if (File.Exists(full))
            {
                var dir = Path.GetDirectoryName(full)!;
                if (!scopes.TryGetValue(dir, out var files))
                    scopes[dir] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full };
                else
                    files?.Add(full);
            }
        }

        var sets = new List<PartSet>();
        var unrecognized = new List<string>();

        foreach (var (dir, explicitFiles) in scopes)
        {
            ct.ThrowIfCancellationRequested();
            var found = DetectInDirectory(dir, explicitFiles, ct);
            if (explicitFiles is null)
            {
                sets.AddRange(found);
                continue;
            }

            var matched = found
                .Where(s => s.Parts.Any(p => explicitFiles.Contains(p.Path))
                         || (s.Crc is not null && explicitFiles.Contains(s.Crc.SourceFile)))
                .ToList();
            sets.AddRange(matched);

            var covered = new HashSet<string>(
                matched.SelectMany(s => s.Parts.Select(p => p.Path).Append(s.Crc?.SourceFile ?? "")),
                StringComparer.OrdinalIgnoreCase);
            unrecognized.AddRange(explicitFiles.Where(f => !covered.Contains(f)));
        }

        sets.Sort((a, b) =>
        {
            int c = string.Compare(a.Directory, b.Directory, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        return new DetectionResult(sets, unrecognized);
    }

    private static IEnumerable<string> SafeSubdirectories(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateDirectories()
                .Where(d => !d.Name.StartsWith(TempPrefix, StringComparison.Ordinal)
                         && (d.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static List<PartSet> DetectInDirectory(string dir, IReadOnlySet<string>? explicitFiles = null, CancellationToken ct = default)
    {
        FileInfo[] files;
        try { files = new DirectoryInfo(dir).GetFiles(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var groups = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Name.StartsWith(TempPrefix, StringComparison.Ordinal))
                continue;
            if (!NamingRules.TryParse(file.Name, out var parsed))
                continue;

            var key = $"{parsed.Scheme}|{parsed.Base}";
            if (parsed.Scheme == NamingScheme.Alphabetic)
                key += "|" + parsed.Padding; // .aa and .aaa runs are different sets
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(new Entry(file, parsed));
        }

        var sets = new List<PartSet>();
        foreach (var (key, entries) in groups)
        {
            ct.ThrowIfCancellationRequested();
            var set = BuildSet(dir, key, entries, explicitFiles);
            if (set is not null)
                sets.Add(set);
        }
        return sets;
    }

    private readonly record struct Entry(FileInfo File, NamingRules.ParsedName Name);

    private static PartSet? BuildSet(string dir, string key, List<Entry> entries, IReadOnlySet<string>? explicitFiles)
    {
        var scheme = entries[0].Name.Scheme;
        var baseName = entries[0].Name.Base;

        entries.Sort((a, b) =>
        {
            int c = a.Name.Index.CompareTo(b.Name.Index);
            return c != 0 ? c : string.Compare(a.File.Name, b.File.Name, StringComparison.OrdinalIgnoreCase);
        });

        bool IsExplicit() => explicitFiles is not null && entries.Any(e => explicitFiles.Contains(e.File.FullName));

        switch (scheme)
        {
            case NamingScheme.RarLegacy when entries.All(e => e.Name.Index == 0):
            case NamingScheme.ZipSpan when entries.All(e => e.Name.Index == NamingRules.ZipLastIndex):
                return null; // a plain .rar / .zip, not a multi-part set

            case NamingScheme.RarLegacy when entries[0].Name.Index > 100 && !IsExplicit():
                return null; // only .s01/.t02-style names and no .rar or .rNN: unlikely to be RAR parts

            case NamingScheme.Numbered or NamingScheme.PartSuffix when entries[0].Name.Index > 1 && !IsExplicit():
                return null; // "report.2023", "notes.2024" … are not parts

            case NamingScheme.Alphabetic:
                // Only a contiguous run starting at .aa counts (so "file.aa" doesn't swallow "file.gz").
                if (entries[0].Name.Index != 0) return null;
                int run = 1;
                while (run < entries.Count && entries[run].Name.Index == entries[run - 1].Name.Index + 1) run++;
                if (run < 2) return null;
                entries = entries.Take(run).ToList();
                break;
        }

        // Two files mapping to the same position (e.g. .part1.rar and .part01.rar) can't both be right.
        var duplicates = new List<string>();
        var unique = new List<Entry>(entries.Count);
        foreach (var e in entries)
        {
            if (unique.Count > 0 && unique[^1].Name.Index == e.Name.Index)
                duplicates.Add($"{unique[^1].File.Name} / {e.File.Name}");
            else
                unique.Add(e);
        }

        int firstIndex = scheme switch
        {
            NamingScheme.RarPart or NamingScheme.ZipSpan => 1,
            NamingScheme.RarLegacy or NamingScheme.Alphabetic => 0,
            _ => unique[0].Name.Index == 0 ? 0 : 1,
        };

        var parts = unique.Select(e => new PartFile(e.File.FullName, e.File.Length, e.Name.Index)).ToList();
        var set = new PartSet
        {
            Key = dir.ToLowerInvariant() + "|" + key.ToLowerInvariant(),
            Directory = dir,
            BaseName = baseName,
            Scheme = scheme,
            Kind = SetKind.SplitFile,
            Parts = parts,
            FirstIndex = firstIndex,
            Padding = unique.Where(e => e.Name.Index != NamingRules.ZipLastIndex).Select(e => e.Name.Padding).DefaultIfEmpty(0).Min(),
        };

        if (duplicates.Count > 0)
        {
            set.Issues.Add(new(IssueSeverity.Error,
                "Two files claim the same position: " + string.Join(", ", duplicates.Take(3)) +
                (duplicates.Count > 3 ? $" and {duplicates.Count - 3} more" : "") +
                " — keep only the right one"));
        }

        CheckNumbering(set, unique);
        bool complete = CheckSequence(set);
        Classify(set);
        CheckSizes(set);
        CheckSignatures(set);
        if (complete)
            CheckArchiveEnd(set);

        return set;
    }

    /// <summary>"x.part1.rar" next to "x.part02.rar" means parts from differently-named sets got mixed.</summary>
    private static void CheckNumbering(PartSet set, List<Entry> entries)
    {
        if (set.Scheme is not (NamingScheme.RarPart or NamingScheme.Numbered or NamingScheme.PartSuffix or NamingScheme.ZipSpan))
            return;

        var odd = entries
            .Where(e => e.Name.Index != NamingRules.ZipLastIndex)
            .Where(e => !NamingRules.HasConsistentPadding(
                e.Name.Index.ToString(CultureInfo.InvariantCulture).PadLeft(e.Name.Padding, '0'), set.Padding))
            .Select(e => e.File.Name)
            .ToList();
        if (odd.Count > 0)
        {
            set.Issues.Add(new(IssueSeverity.Error,
                $"Part numbering doesn't match ({string.Join(", ", odd.Take(3))}{(odd.Count > 3 ? ", …" : "")}) — " +
                "these files may come from different sets"));
        }
    }

    /// <summary>Flags missing parts. Returns true when no gaps were found. Works on gaps, so huge sets stay cheap.</summary>
    private static bool CheckSequence(PartSet set)
    {
        var indices = set.Parts.Select(p => p.Index).Where(i => i != NamingRules.ZipLastIndex).ToList();

        bool missingFirst = indices.Count == 0 ? set.Scheme != NamingScheme.ZipSpan : indices[0] != set.FirstIndex;
        var missingLabels = new List<string>();
        long missingCount = 0;

        int expected = set.FirstIndex;
        if (missingFirst && indices.Count > 0)
        {
            // Everything before the first present part (beyond the first part itself) is missing too.
            missingCount += Math.Max(0, indices[0] - set.FirstIndex - 1);
            for (int i = set.FirstIndex + 1; i < indices[0] && missingLabels.Count < 4; i++)
                missingLabels.Add(set.PartLabel(i));
            expected = indices[0];
        }
        foreach (var index in indices)
        {
            if (index > expected)
            {
                missingCount += index - expected;
                for (int i = expected; i < index && missingLabels.Count < 4; i++)
                    missingLabels.Add(set.PartLabel(i));
            }
            expected = index + 1;
        }

        if (missingFirst)
            set.Issues.Add(new(IssueSeverity.Error, $"Missing the first part ({set.PartLabel(set.FirstIndex)})"));
        if (missingCount > 0)
        {
            var labels = string.Join(", ", missingLabels);
            if (missingCount > missingLabels.Count) labels += $" and {missingCount - missingLabels.Count:N0} more";
            set.Issues.Add(new(IssueSeverity.Error, "Missing " + labels));
        }

        bool complete = !missingFirst && missingCount == 0;

        if (set.Scheme == NamingScheme.ZipSpan && set.Parts[^1].Index != NamingRules.ZipLastIndex)
        {
            set.Issues.Add(new(IssueSeverity.Error, "Missing the final .zip part"));
            complete = false;
        }

        foreach (var p in set.Parts.Where(p => p.Size == 0).Take(3))
            set.Issues.Add(new(IssueSeverity.Error, $"{p.Name} is empty (0 bytes)"));

        if (set.Parts.Count == 1 && set.Scheme is NamingScheme.Numbered or NamingScheme.PartSuffix)
            set.Issues.Add(new(IssueSeverity.Warning, "Only one part found"));

        return complete;
    }

    private static void Classify(PartSet set)
    {
        switch (set.Scheme)
        {
            case NamingScheme.RarPart:
            case NamingScheme.RarLegacy:
                set.Kind = SetKind.RarVolumes;
                set.Format = ArchiveFormat.Rar;
                return;
            case NamingScheme.ZipSpan:
                set.Kind = SetKind.ZipSpanned;
                set.Format = ArchiveFormat.Zip;
                return;
        }

        set.Crc = FindCrcFile(set);
        var name = set.Crc?.FileName ?? set.BaseName;
        var byName = FormatInfo.FromFileName(name);

        ArchiveFormat sniffed = ArchiveFormat.Unknown;
        if (set.Parts[0].Index == set.FirstIndex)
            sniffed = FormatInfo.Sniff(FormatInfo.ReadHead(set.FirstPartPath, HeadBytes));

        if (byName != ArchiveFormat.Unknown)
        {
            set.Kind = SetKind.SplitArchive;
            set.Format = byName;
        }
        else if (!Path.HasExtension(name) && sniffed != ArchiveFormat.Unknown)
        {
            set.Kind = SetKind.SplitArchive;
            set.Format = sniffed;
        }
        else
        {
            set.Kind = SetKind.SplitFile;
            set.Format = ArchiveFormat.Unknown;
        }

        if (set.Crc?.Size is long expected && !set.IsBlocked && expected != set.TotalSize)
        {
            set.Issues.Add(new(IssueSeverity.Error,
                $"Size doesn't match the .crc file (expected {Bytes.Format(expected)}, found {Bytes.Format(set.TotalSize)})"));
        }
    }

    private static CrcReference? FindCrcFile(PartSet set)
    {
        var candidates = new List<string> { Path.Combine(set.Directory, set.BaseName + ".crc") };
        if (Path.HasExtension(set.BaseName))
            candidates.Add(Path.Combine(set.Directory, Path.GetFileNameWithoutExtension(set.BaseName) + ".crc"));

        foreach (var candidate in candidates)
        {
            var crc = FormatInfo.ReadCrcFile(candidate);
            if (crc is not null)
                return crc;
        }
        return null;
    }

    /// <summary>
    /// Every part except the last must be the same size: splitters (WinRAR, 7-Zip, HJSplit, split …) cut at a
    /// fixed size. A different size means a broken download or a part from another set.
    /// </summary>
    private static void CheckSizes(PartSet set)
    {
        var parts = set.Parts;
        if (parts.Count < 2) return;

        // Self-extracting first volumes carry the SFX module, so they're naturally a different size.
        int start = parts[0].Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        // The trailing .zip of a spanned set holds the central directory and differs too (it's the last part).
        var body = parts.Skip(start).Take(parts.Count - 1 - start).Where(p => p.Size > 0).ToList();
        if (body.Count == 0) return;

        long reference = body.GroupBy(p => p.Size).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        var odd = body.Where(p => p.Size != reference).ToList();
        if (odd.Count > 0)
        {
            var names = string.Join(", ", odd.Take(3).Select(p => $"{p.Name} ({Bytes.Format(p.Size)})"));
            if (odd.Count > 3) names += $" and {odd.Count - 3} more";
            set.Issues.Add(new(IssueSeverity.Error,
                $"Part sizes don't match: {names} vs {Bytes.Format(reference)} for the others — " +
                "a part is incomplete or from a different set"));
        }

        bool byteSplit = set.Scheme is NamingScheme.Numbered or NamingScheme.PartSuffix or NamingScheme.Alphabetic;
        if ((byteSplit || set.Kind == SetKind.RarVolumes) && parts[^1].Size > reference)
        {
            set.Issues.Add(new(IssueSeverity.Error,
                $"{parts[^1].Name} is larger than the other parts ({Bytes.Format(parts[^1].Size)} vs {Bytes.Format(reference)}) — " +
                "it may be from a different set"));
        }
    }

    /// <summary>Each part's own header must agree with where it sits in the set.</summary>
    private static void CheckSignatures(PartSet set)
    {
        bool firstPresent = set.Parts[0].Index == set.FirstIndex;

        switch (set.Kind)
        {
            case SetKind.RarVolumes:
                CheckRarVolumes(set);
                break;

            case SetKind.ZipSpanned when firstPresent:
                var zipHead = FormatInfo.ReadHead(set.FirstPartPath, 8);
                if (zipHead.Length >= 4 && FormatInfo.Sniff(zipHead) != ArchiveFormat.Zip)
                    set.Issues.Add(new(IssueSeverity.Error, $"{set.Parts[0].Name} doesn't start like a ZIP archive — it's damaged or from a different set"));
                break;

            case SetKind.SplitArchive when firstPresent:
                var head = FormatInfo.ReadHead(set.FirstPartPath, HeadBytes);
                if (head.Length > 0 && !FormatInfo.StartsLike(set.Format, head))
                {
                    set.Issues.Add(new(IssueSeverity.Error,
                        $"{set.Parts[0].Name} doesn't start like a {FormatInfo.Extension(set.Format)} archive — " +
                        "the parts are damaged or don't belong together"));
                }
                break;
        }
    }

    private static void CheckRarVolumes(PartSet set)
    {
        var problems = new List<string>();
        int? version = null;

        for (int position = 0; position < set.Parts.Count; position++)
        {
            var part = set.Parts[position];
            // Position within the archive's volume sequence (only meaningful when nothing is missing before it).
            int expectedVolume = part.Index - set.FirstIndex;
            bool sfx = part.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (sfx) continue; // the RAR data starts after the SFX module

            var head = FormatInfo.ReadHead(part.Path, HeadBytes);
            if (head.Length == 0) continue;

            if (FormatInfo.Sniff(head) != ArchiveFormat.Rar)
            {
                problems.Add($"{part.Name} isn't a RAR volume");
                continue;
            }

            var header = FormatInfo.ReadRarVolumeHeader(head);
            if (header is null) continue; // encrypted headers or unknown layout: nothing to compare

            if (version is null) version = header.Version;
            else if (version != header.Version)
                problems.Add($"{part.Name} is RAR{header.Version} while the others are RAR{version}");

            if (!header.IsVolume && set.Parts.Count > 1)
                problems.Add($"{part.Name} is a complete archive on its own, not a volume");
            else if (expectedVolume > 0 && header.ClaimsFirstVolume)
                problems.Add($"{part.Name} is the first volume of a different archive");
            else if (header.VolumeNumber is int number && number != expectedVolume)
                problems.Add($"{part.Name} is actually volume {number + 1} of its archive");
        }

        if (problems.Count > 0)
        {
            set.Issues.Add(new(IssueSeverity.Error,
                "Volumes don't match: " + string.Join("; ", problems.Take(3)) +
                (problems.Count > 3 ? $" and {problems.Count - 3} more" : "")));
        }
    }

    /// <summary>Uses the archive's own headers to detect parts missing from (or extra at) the end.</summary>
    private static void CheckArchiveEnd(PartSet set)
    {
        if (set.IsBlocked) return;

        // 7z: the start header records exactly how long the archive is.
        if (set.Scheme is NamingScheme.Numbered or NamingScheme.PartSuffix or NamingScheme.Alphabetic)
        {
            var expected = FormatInfo.SevenZipExpectedSize(FormatInfo.ReadHead(set.FirstPartPath, 32));
            if (expected is long size && set.TotalSize < size)
            {
                set.Issues.Add(new(IssueSeverity.Error,
                    $"Incomplete: {Bytes.Format(size - set.TotalSize)} is missing — the last part(s) may not have finished downloading"));
            }
            else if (expected is long size2 && set.TotalSize > size2)
            {
                set.Issues.Add(new(IssueSeverity.Error,
                    $"The parts hold {Bytes.Format(set.TotalSize - size2)} more than this archive needs — " +
                    "an extra part or one from a different archive is mixed in"));
            }
            return;
        }

        // RAR: the last volume says whether more volumes follow it.
        if (set.Kind == SetKind.RarVolumes)
        {
            var end = FormatInfo.ReadRarVolumeEnd(set.Parts[^1].Path);
            if (end == FormatInfo.RarVolumeEnd.MoreVolumesFollow)
            {
                set.Issues.Add(new(IssueSeverity.Error, $"More parts are expected after {set.Parts[^1].Name}"));
            }
            else if (end == FormatInfo.RarVolumeEnd.Unknown && set.Parts.Count == 1)
            {
                set.Issues.Add(new(IssueSeverity.Warning, "Only one volume found"));
            }
            return;
        }

        // Spanned ZIP: the final .zip records how many parts there are.
        if (set.Kind == SetKind.ZipSpanned)
        {
            var count = FormatInfo.ZipSpannedPartCount(set.Parts[^1].Path);
            if (count is int expectedParts && expectedParts != set.Parts.Count)
            {
                set.Issues.Add(new(IssueSeverity.Error, $"Expected {expectedParts} parts but found {set.Parts.Count}"));
            }
        }
    }
}
