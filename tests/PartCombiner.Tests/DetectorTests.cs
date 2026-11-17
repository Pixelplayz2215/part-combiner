using System.IO.Hashing;
using PartCombiner.Core;
using static PartCombiner.Tests.TestUtil;

namespace PartCombiner.Tests;

public class DetectorTests
{
    private static readonly byte[] ZipSpanHeader = [0x50, 0x4B, 0x07, 0x08];
    private static readonly byte[] ZipHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] SevenZipHeader = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    // ----- RAR -----

    [Fact]
    public void RarPartVolumes_AreGroupedInOrder()
    {
        using var d = new TempDir();
        for (int i = 1; i <= 9; i++) d.File($"Holiday Photos.part{i}.rar", Rar5Volume(i - 1, 100));
        d.File("Holiday Photos.part10.rar", Rar5Volume(9, 40));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.RarVolumes, set.Kind);
        Assert.Equal("Holiday Photos", set.DisplayName);
        Assert.Equal(10, set.Parts.Count);
        Assert.EndsWith("part1.rar", set.FirstPartPath);
        Assert.EndsWith("part10.rar", set.Parts[^1].Path);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void ThreeRarParts_LikeAUsualDownload_AreReady()
    {
        using var d = new TempDir();
        d.File("Game.part1.rar", Rar5Volume(0, 500));
        d.File("Game.part2.rar", Rar5Volume(1, 500));
        d.File("Game.part3.rar", Rar5Volume(2, 120));

        var set = Single(PartDetector.Detect([d["Game.part2.rar"]])); // dropping any one part is enough
        Assert.Equal(3, set.Parts.Count);
        Assert.True(set.MustExtract);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void RarPartVolumes_MissingMiddlePart_IsBlocked()
    {
        using var d = new TempDir();
        d.File("x.part01.rar", Rar5Volume(0, 100));
        d.File("x.part03.rar", Rar5Volume(2, 100));
        d.File("x.part04.rar", Rar5Volume(3, 10));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.True(set.IsBlocked);
        Assert.Contains("Missing part 2", Errors(set));
    }

    [Fact]
    public void RarPartVolumes_MissingFirstPart_IsBlocked()
    {
        using var d = new TempDir();
        d.File("x.part2.rar", Rar5Volume(1, 100));
        d.File("x.part3.rar", Rar5Volume(2, 10));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("Missing the first part", Errors(set));
    }

    [Fact]
    public void SelfExtractingFirstVolume_IsAccepted()
    {
        using var d = new TempDir();
        d.File("setup.part1.exe", 300);
        d.File("setup.part2.rar", Rar5Volume(1, 100));
        d.File("setup.part3.rar", Rar5Volume(2, 100));
        d.File("setup.part4.rar", Rar5Volume(3, 50));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.RarVolumes, set.Kind);
        Assert.EndsWith("part1.exe", set.FirstPartPath);
        Assert.Empty(set.Issues); // the SFX stub makes the first volume bigger; that's not an issue
    }

    [Fact]
    public void LegacyRarVolumes_StartWithDotRar()
    {
        using var d = new TempDir();
        d.File("movie.rar", Rar4Volume(true, 100));
        d.File("movie.r00", Rar4Volume(false, 100));
        d.File("movie.r01", Rar4Volume(false, 100));
        d.File("movie.r02", Rar4Volume(false, 20));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.RarVolumes, set.Kind);
        Assert.Equal(["movie.rar", "movie.r00", "movie.r01", "movie.r02"], set.Parts.Select(p => p.Name));
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void LegacyRarVolumes_BeyondR99_ContinueWithS00()
    {
        using var d = new TempDir();
        d.File("big.rar", Rar4Volume(true, 64));
        for (int i = 0; i < 100; i++) d.File($"big.r{i:00}", Rar4Volume(false, 64));
        for (int i = 0; i < 30; i++) d.File($"big.s{i:00}", Rar4Volume(false, i == 29 ? 30 : 64));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(131, set.Parts.Count);
        Assert.Equal("big.s29", set.Parts[^1].Name);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void LegacyRarVolumes_MissingDotRar_IsBlocked()
    {
        using var d = new TempDir();
        d.File("movie.r00", Rar4Volume(false, 100));
        d.File("movie.r01", Rar4Volume(false, 20));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("Missing the first part (.rar)", Errors(set));
    }

    // ----- Mismatched parts are blocked -----

    [Fact]
    public void RarVolume_WithTheWrongVolumeNumber_IsBlocked()
    {
        using var d = new TempDir();
        d.File("x.part1.rar", Rar5Volume(0, 100));
        d.File("x.part2.rar", Rar5Volume(1, 100));
        d.File("x.part3.rar", Rar5Volume(4, 100)); // really part 5 of something, renamed
        d.File("x.part4.rar", Rar5Volume(3, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.True(set.IsBlocked);
        Assert.Contains("x.part3.rar is actually volume 5", Errors(set));
    }

    [Fact]
    public void RarVolume_FromAnotherArchive_IsBlocked()
    {
        using var d = new TempDir();
        d.File("x.part1.rar", Rar5Volume(0, 100));
        d.File("x.part2.rar", Rar5Volume(0, 100)); // a first volume in second position
        d.File("x.part3.rar", Rar5Volume(2, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("x.part2.rar is the first volume of a different archive", Errors(set));
    }

    [Fact]
    public void Rar4Volume_ClaimingToBeFirst_IsBlocked()
    {
        using var d = new TempDir();
        d.File("old.rar", Rar4Volume(true, 100));
        d.File("old.r00", Rar4Volume(true, 100));
        d.File("old.r01", Rar4Volume(false, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("old.r00 is the first volume of a different archive", Errors(set));
    }

    [Fact]
    public void MixedRarVersions_AreBlocked()
    {
        using var d = new TempDir();
        d.File("m.part1.rar", Rar5Volume(0, 100));
        d.File("m.part2.rar", Rar4Volume(false, 100));
        d.File("m.part3.rar", Rar5Volume(2, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("RAR4 while the others are RAR5", Errors(set));
    }

    [Fact]
    public void NonRarFile_NamedLikeAVolume_IsBlocked()
    {
        using var d = new TempDir();
        d.File("n.part1.rar", Rar5Volume(0, 100));
        d.File("n.part2.rar", 100); // not a RAR file at all
        d.File("n.part3.rar", Rar5Volume(2, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("n.part2.rar isn't a RAR volume", Errors(set));
    }

    [Fact]
    public void StandaloneArchive_InAVolumeSet_IsBlocked()
    {
        using var d = new TempDir();
        d.File("s.part1.rar", Rar5Volume(0, 100));
        d.File("s.part2.rar", Rar5Volume(0, 100, isVolume: false));
        d.File("s.part3.rar", Rar5Volume(2, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("complete archive on its own", Errors(set));
    }

    [Fact]
    public void PartOfDifferentSize_IsBlocked()
    {
        using var d = new TempDir();
        d.File("f.bin.001", 100);
        d.File("f.bin.002", 60);
        d.File("f.bin.003", 100);
        d.File("f.bin.004", 20);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.True(set.IsBlocked);
        Assert.Contains("Part sizes don't match: f.bin.002", Errors(set));
    }

    [Fact]
    public void OversizedLastPart_IsBlocked()
    {
        using var d = new TempDir();
        d.File("f.bin.001", 100);
        d.File("f.bin.002", 100);
        d.File("f.bin.003", 180);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("f.bin.003 is larger than the other parts", Errors(set));
    }

    [Fact]
    public void DuplicatePositions_AreBlocked()
    {
        using var d = new TempDir();
        d.File("dup.part1.rar", Rar5Volume(0, 100));
        d.File("dup.part01.rar", Rar5Volume(0, 100));
        d.File("dup.part2.rar", Rar5Volume(1, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("Two files claim the same position", Errors(set));
    }

    [Fact]
    public void InconsistentNumbering_IsBlocked()
    {
        using var d = new TempDir();
        d.File("mix.part1.rar", Rar5Volume(0, 100));
        d.File("mix.part02.rar", Rar5Volume(1, 100));
        d.File("mix.part3.rar", Rar5Volume(2, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("Part numbering doesn't match (mix.part02.rar)", Errors(set));
    }

    [Fact]
    public void SplitArchive_WhoseFirstPartIsNotThatFormat_IsBlocked()
    {
        using var d = new TempDir();
        d.File("photos.7z.001", WithHeader(ZipHeader, 100)); // a zip, not a 7z
        d.File("photos.7z.002", 40);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("doesn't start like a .7z archive", Errors(set));
    }

    // ----- No limit on the number of parts -----

    [Fact]
    public void ThousandsOfNumberedParts_AreSupported()
    {
        using var d = new TempDir();
        for (int i = 1; i <= 1500; i++)
            d.File($"huge.bin.{i:000}", i == 1500 ? 7 : 16); // .001 … .999, then .1000 … .1500

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(1500, set.Parts.Count);
        Assert.Equal("huge.bin.1500", set.Parts[^1].Name);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void HundredsOfRarParts_AreSupported()
    {
        using var d = new TempDir();
        for (int i = 1; i <= 250; i++)
            d.File($"many.part{i:000}.rar", Rar5Volume(i - 1, i == 250 ? 40 : 64));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(250, set.Parts.Count);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void HugeGap_IsReportedWithoutListingEveryPart()
    {
        using var d = new TempDir();
        d.File("gap.part1.rar", Rar5Volume(0, 100));
        d.File("gap.part999999.rar", Rar5Volume(999998, 30));

        var set = Single(PartDetector.Detect([d.Path]));
        var errors = Errors(set);
        Assert.Contains("Missing part 2, part 3, part 4, part 5 and", errors);
        Assert.Contains("more", errors);
    }

    // ----- Plain splits and other namings -----

    [Fact]
    public void PlainArchives_AreNotSets()
    {
        using var d = new TempDir();
        d.File("single.rar", 100);
        d.File("single.zip", 100);
        d.File("notes.txt", 10);
        d.File("report.2023", 10);
        d.File("report.2024", 10);

        Assert.Empty(PartDetector.Detect([d.Path]).Sets);
    }

    [Fact]
    public void SpannedZip_EndsWithDotZip()
    {
        using var d = new TempDir();
        d.File("docs.z02", 100);
        d.File("docs.zip", 30);
        d.File("docs.z01", WithHeader(ZipSpanHeader, 100));

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.ZipSpanned, set.Kind);
        Assert.Equal(["docs.z01", "docs.z02", "docs.zip"], set.Parts.Select(p => p.Name));
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void SpannedZip_WithoutDotZip_IsBlocked()
    {
        using var d = new TempDir();
        d.File("docs.z01", WithHeader(ZipSpanHeader, 100));
        d.File("docs.z02", 100);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("final .zip", Errors(set));
    }

    [Fact]
    public void NumberedSplitOfAVideo_IsAPlainFileJoin()
    {
        using var d = new TempDir();
        d.File("movie.mkv.001", 100);
        d.File("movie.mkv.002", 100);
        d.File("movie.mkv.003", 30);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.SplitFile, set.Kind);
        Assert.Equal("movie.mkv", set.JoinedFileName);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void NumberedSplit_WithoutExtension_IsSniffedAs7z()
    {
        using var d = new TempDir();
        d.File("backup.001", WithHeader(SevenZipHeader, 100));
        d.File("backup.002", 50);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.SplitArchive, set.Kind);
        Assert.Equal(ArchiveFormat.SevenZip, set.Format);
        Assert.Equal("backup.7z", set.JoinedFileName);
    }

    [Fact]
    public void NumberedSplit_StartingAtZero_IsComplete()
    {
        using var d = new TempDir();
        d.File("disk.iso.000", 100);
        d.File("disk.iso.001", 100);
        d.File("disk.iso.002", 10);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(0, set.FirstIndex);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void PartSuffixSplit_OfZip_IsAnArchive()
    {
        using var d = new TempDir();
        d.File("project.zip.part1", WithHeader(ZipHeader, 100));
        d.File("project.zip.part2", 40);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.SplitArchive, set.Kind);
        Assert.Equal(ArchiveFormat.Zip, set.Format);
        Assert.Equal("project", set.ExtractFolderName);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void UnixSplit_TakesOnlyTheContiguousRun()
    {
        using var d = new TempDir();
        d.File("image.iso.aa", 100);
        d.File("image.iso.ab", 100);
        d.File("image.iso.ac", 10);
        d.File("image.iso.gz", 10); // unrelated file with a two-letter extension

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(NamingScheme.Alphabetic, set.Scheme);
        Assert.Equal(3, set.Parts.Count);
        Assert.Equal("image.iso", set.JoinedFileName);
    }

    [Fact]
    public void UnixSplit_WithLongerSuffixes_RollsOverCorrectly()
    {
        using var d = new TempDir();
        var names = new List<string>();
        for (char a = 'a'; a <= 'a'; a++)
            for (char b = 'a'; b <= 'b'; b++)
                for (char c = 'a'; c <= 'z'; c++)
                    names.Add($"{a}{b}{c}");
        foreach (var n in names) d.File($"vm.img.{n}", n == names[^1] ? 5 : 10);

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(52, set.Parts.Count);
        Assert.Equal("vm.img.abz", set.Parts[^1].Name);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void DroppingOnePart_FindsItsSiblings_AndReportsUnrelatedFiles()
    {
        using var d = new TempDir();
        var p2 = d.File("a.7z.002", 50);
        d.File("a.7z.001", WithHeader(SevenZipHeader, 100));
        d.File("other.7z.001", WithHeader(SevenZipHeader, 100));
        var txt = d.File("readme.txt", 10);

        var result = PartDetector.Detect([p2, txt]);
        var set = Single(result);
        Assert.Equal("a.7z", set.BaseName);
        Assert.Equal(2, set.Parts.Count);
        Assert.Equal([txt], result.UnrecognizedFiles);
    }

    [Fact]
    public void TotalCommanderCrcFile_ProvidesNameAndSize()
    {
        using var d = new TempDir();
        d.File("movie.001", 100);
        d.File("movie.002", 50);
        File.WriteAllText(d["movie.crc"], "filename=movie.mkv\r\nsize=150\r\ncrc32=0BADF00D\r\n");

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal("movie.mkv", set.JoinedFileName);
        Assert.Equal(0x0BADF00Du, set.Crc!.Crc32);
        Assert.False(set.IsBlocked, Errors(set));

        File.WriteAllText(d["movie.crc"], "filename=movie.mkv\r\nsize=999\r\n");
        set = Single(PartDetector.Detect([d.Path]));
        Assert.Contains("doesn't match the .crc", Errors(set));
    }

    [Fact]
    public void OwnTempFiles_AreIgnored()
    {
        using var d = new TempDir();
        d.File(".pc-1234.7z.001", 100);
        d.File(".pc-1234.7z.002", 100);

        Assert.Empty(PartDetector.Detect([d.Path]).Sets);
    }

    [Fact]
    public void SubfoldersAreScannedOneLevelDeep()
    {
        using var d = new TempDir();
        d.File(Path.Combine("one", "a.part1.rar"), Rar5Volume(0, 10));
        d.File(Path.Combine("one", "a.part2.rar"), Rar5Volume(1, 5));
        d.File(Path.Combine("two", "b.7z.001"), WithHeader(SevenZipHeader, 10));

        var sets = PartDetector.Detect([d.Path]).Sets;
        Assert.Equal(2, sets.Count);
    }

    // ----- Header parsers -----

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(300, false)] // multi-byte vint
    public void Rar5MainHeader_IsParsed(int volume, bool claimsFirst)
    {
        var header = FormatInfo.ReadRarVolumeHeader(Rar5Volume(volume, 64));
        Assert.NotNull(header);
        Assert.Equal(5, header.Version);
        Assert.True(header.IsVolume);
        Assert.Equal(volume, header.VolumeNumber);
        Assert.Equal(claimsFirst, header.ClaimsFirstVolume);
    }

    [Fact]
    public void Rar5MainHeader_WithBadCrc_IsUnknown()
    {
        var bytes = Rar5Volume(2, 64);
        bytes[8] ^= 0xFF;
        Assert.Null(FormatInfo.ReadRarVolumeHeader(bytes));
    }

    [Theory]
    [InlineData(0x00, FormatInfo.RarVolumeEnd.LastVolume)]
    [InlineData(0x01, FormatInfo.RarVolumeEnd.MoreVolumesFollow)]
    public void Rar5EndOfArchive_IsParsed(byte endFlags, FormatInfo.RarVolumeEnd expected)
    {
        byte[] header = [0x03, 0x05, 0x00, endFlags];
        var tail = RandomBytes(40, 7).Concat(BitConverter.GetBytes(Crc32.HashToUInt32(header))).Concat(header).ToArray();
        Assert.Equal(expected, FormatInfo.ParseRarVolumeEnd(tail));
    }

    [Theory]
    [InlineData(0x0000, FormatInfo.RarVolumeEnd.LastVolume)]
    [InlineData(0x0001, FormatInfo.RarVolumeEnd.MoreVolumesFollow)]
    [InlineData(0x0009, FormatInfo.RarVolumeEnd.MoreVolumesFollow)] // with volume number field
    public void Rar4EndOfArchive_IsParsed(ushort flags, FormatInfo.RarVolumeEnd expected)
    {
        var body = new List<byte> { 0x7B, (byte)flags, (byte)(flags >> 8) };
        int size = 7 + ((flags & 0x0008) != 0 ? 2 : 0);
        body.AddRange([(byte)size, 0x00]);
        if ((flags & 0x0008) != 0) body.AddRange([0x02, 0x00]);
        ushort crc = (ushort)(Crc32.HashToUInt32(body.ToArray()) & 0xFFFF);
        var tail = RandomBytes(30, 3).Concat(BitConverter.GetBytes(crc)).Concat(body).ToArray();
        Assert.Equal(expected, FormatInfo.ParseRarVolumeEnd(tail));
    }

    [Fact]
    public void RandomTail_IsUnknown()
    {
        Assert.Equal(FormatInfo.RarVolumeEnd.Unknown, FormatInfo.ParseRarVolumeEnd(RandomBytes(64, 11)));
    }
}
