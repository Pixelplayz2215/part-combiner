namespace PartCombiner.Core;

/// <summary>What a group of parts represents and therefore how it gets combined.</summary>
public enum SetKind
{
    /// <summary>A RAR multi-volume archive (name.part1.rar … or name.rar + name.r00 …). Must be extracted.</summary>
    RarVolumes,

    /// <summary>A spanned ZIP archive (name.z01 … + name.zip). Must be extracted.</summary>
    ZipSpanned,

    /// <summary>An archive file that was cut into byte chunks (name.7z.001, name.zip.part1 …). Can be extracted or re-joined.</summary>
    SplitArchive,

    /// <summary>Any other file that was cut into byte chunks (movie.mkv.001, disk.iso.part1 …). Gets re-joined.</summary>
    SplitFile,
}

/// <summary>How the part files are named.</summary>
public enum NamingScheme
{
    /// <summary>name.part1.rar, name.part02.rar (first part may be .part1.exe for self-extractors).</summary>
    RarPart,

    /// <summary>name.rar, name.r00, name.r01 … name.s00 …</summary>
    RarLegacy,

    /// <summary>name.z01, name.z02 … name.zip</summary>
    ZipSpan,

    /// <summary>name.001, name.002 … (7-Zip, HJSplit, Total Commander, FFSJ …)</summary>
    Numbered,

    /// <summary>name.part1, name.part2 …</summary>
    PartSuffix,

    /// <summary>name.aa, name.ab … (Unix split)</summary>
    Alphabetic,
}

public enum ArchiveFormat
{
    Unknown,
    SevenZip,
    Rar,
    Zip,
    Tar,
    TarGz,
    TarBz2,
    TarXz,
    TarZst,
}

public enum IssueSeverity
{
    /// <summary>Something looks off, but combining can still be attempted.</summary>
    Warning,

    /// <summary>The set cannot be combined as-is (for example a part is missing).</summary>
    Error,
}

public sealed record SetIssue(IssueSeverity Severity, string Message);

public sealed record PartFile(string Path, long Size, int Index)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Checksum information from a Total Commander style <c>.crc</c> file.</summary>
public sealed record CrcReference(string SourceFile, string? FileName, long? Size, uint? Crc32);

/// <summary>A group of part files that belong together.</summary>
public sealed class PartSet
{
    public required string Key { get; init; }

    /// <summary>Folder that holds the parts.</summary>
    public required string Directory { get; init; }

    /// <summary>
    /// File name with the part suffix removed: "Photos" for Photos.part1.rar,
    /// "movie.mkv" for movie.mkv.001, "backup.7z" for backup.7z.001.
    /// </summary>
    public required string BaseName { get; init; }

    public required NamingScheme Scheme { get; init; }

    public required SetKind Kind { get; set; }

    public ArchiveFormat Format { get; set; }

    /// <summary>Parts in the order they must be read.</summary>
    public required IReadOnlyList<PartFile> Parts { get; init; }

    public List<SetIssue> Issues { get; } = [];

    public CrcReference? Crc { get; set; }

    /// <summary>Index the first part is expected to have (0 or 1 depending on the scheme).</summary>
    public int FirstIndex { get; init; }

    /// <summary>Zero-padding width used by numbered parts (3 for .001).</summary>
    public int Padding { get; init; }

    public long TotalSize => Parts.Sum(p => p.Size);

    public string FirstPartPath => Parts[0].Path;

    public bool IsBlocked => Issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>True when this set is an archive whose contents can be unpacked.</summary>
    public bool IsArchive => Kind != SetKind.SplitFile;

    /// <summary>True when the only sensible result is to extract (volumes can't be byte-joined).</summary>
    public bool MustExtract => Kind is SetKind.RarVolumes or SetKind.ZipSpanned;

    public string DisplayName => Crc?.FileName ?? BaseName;

    /// <summary>A short description of the naming, e.g. ".part1.rar" or ".001".</summary>
    public string SchemeLabel => Scheme switch
    {
        NamingScheme.RarPart => ".part1.rar",
        NamingScheme.RarLegacy => ".rar + .r00",
        NamingScheme.ZipSpan => ".z01 + .zip",
        NamingScheme.Numbered => "." + FirstIndex.ToString().PadLeft(Math.Max(Padding, 3), '0'),
        NamingScheme.PartSuffix => ".part" + FirstIndex,
        NamingScheme.Alphabetic => ".aa",
        _ => "",
    };

    /// <summary>Name for a folder that holds extracted contents ("Photos" for Photos.7z).</summary>
    public string ExtractFolderName
    {
        get
        {
            var name = Crc?.FileName ?? BaseName;
            var stripped = FormatInfo.StripArchiveExtension(name);
            return string.IsNullOrWhiteSpace(stripped) ? name : stripped;
        }
    }

    /// <summary>File name for the re-joined file.</summary>
    public string JoinedFileName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Crc?.FileName))
                return Crc!.FileName!;
            if (Kind == SetKind.SplitArchive && !System.IO.Path.HasExtension(BaseName))
                return BaseName + FormatInfo.Extension(Format);
            return BaseName;
        }
    }

    /// <summary>Human-readable label for a part position, used in "missing" messages.</summary>
    public string PartLabel(int index) => NamingRules.Label(Scheme, index, Padding);
}
