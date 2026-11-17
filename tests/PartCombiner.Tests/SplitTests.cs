using PartCombiner.Core;
using static PartCombiner.Tests.TestUtil;

namespace PartCombiner.Tests;

/// <summary>Splitting, and that every split comes back together through the app's own detect + combine.</summary>
public class SplitTests
{
    [Fact]
    public void PartNames_FollowEachStyle()
    {
        Assert.Equal(["a.mkv.001", "a.mkv.002", "a.mkv.003"], FileSplitter.PartNames("a.mkv", SplitStyle.Numbered, 3));
        Assert.Equal("a.mkv.1000", FileSplitter.PartNames("a.mkv", SplitStyle.Numbered, 1000)[^1]);
        Assert.Equal("a.mkv.0001", FileSplitter.PartNames("a.mkv", SplitStyle.Numbered, 1000)[0]);
        var part = FileSplitter.PartNames("a.mkv", SplitStyle.PartSuffix, 12);
        Assert.Equal("a.mkv.part01", part[0]);
        Assert.Equal("a.mkv.part12", part[11]);
        var letters = FileSplitter.PartNames("a.mkv", SplitStyle.Alphabetic, 27);
        Assert.Equal("a.mkv.aa", letters[0]);
        Assert.Equal("a.mkv.ab", letters[1]);
        Assert.Equal("a.mkv.ba", letters[26]);
        Assert.Equal("a.7z.002", FileSplitter.PartNames("a.7z", SplitStyle.SevenZip, 2)[1]);
    }

    [Theory]
    [InlineData(1000, 3, 334, 3)]
    [InlineData(10, 6, 2, 5)]   // too small for 6 equal parts
    [InlineData(10, 5, 2, 5)]
    [InlineData(1_000_000, 7, 142_858, 7)]
    public void PartSize_AndActualCount(long total, int asked, long size, int actual)
    {
        Assert.Equal(size, FileSplitter.PartSize(total, asked));
        Assert.Equal(actual, FileSplitter.ActualCount(total, asked));
    }

    [Theory]
    [InlineData(SplitStyle.Numbered, 4)]
    [InlineData(SplitStyle.PartSuffix, 12)]
    [InlineData(SplitStyle.Alphabetic, 30)]
    public async Task PlainSplit_RejoinsByteForByte(SplitStyle style, int parts)
    {
        using var d = new TempDir();
        var original = RandomBytes(1_000_003, 42);
        var source = d.File(Path.Combine("src", "clip.mkv"), original);

        var result = await FileSplitter.SplitAsync(
            new SplitOptions { Source = source, Parts = parts, Style = style, OutputDirectory = d["parts"] }, SevenZip, null, default);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Verified);
        Assert.Equal(parts, Directory.GetFiles(d["parts"]).Length);
        Assert.All(result.Parts.SkipLast(1), p => Assert.Equal(result.PartSize, new FileInfo(p).Length));

        var set = Single(PartDetector.Detect([result.Parts[^1]])); // any part finds the rest
        Assert.Equal(parts, set.Parts.Count);
        var joined = await Combine(set, extract: false, output: d["out"]);
        Assert.True(joined.Success, joined.Error);
        Assert.Equal(d[Path.Combine("out", "clip.mkv")], joined.OutputPath);
        Assert.True(File.ReadAllBytes(joined.OutputPath!).AsSpan().SequenceEqual(original));
    }

    [Fact]
    public async Task CrcFile_IsWritten_AndChecksTheJoin()
    {
        using var d = new TempDir();
        var original = RandomBytes(300_000, 7);
        var source = d.File("video.mp4", original);

        var result = await FileSplitter.SplitAsync(
            new SplitOptions { Source = source, Parts = 3, OutputDirectory = d["parts"], WriteCrc = true }, null, null, default);
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(d[Path.Combine("parts", "video.mp4.crc")]));

        var set = Single(PartDetector.Detect([d["parts"]]));
        var joined = await Combine(set, output: d["out"]);
        Assert.True(joined.ChecksumVerified);
        Assert.True(File.ReadAllBytes(joined.OutputPath!).AsSpan().SequenceEqual(original));
    }

    [Theory]
    [InlineData(SplitStyle.SevenZip)]
    [InlineData(SplitStyle.Zip)]
    public async Task FolderAsArchive_ExtractsBackToTheSameFolder(SplitStyle style)
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Holiday Photos"));

        var result = await FileSplitter.SplitAsync(
            new SplitOptions { Source = src, Parts = 5, Style = style, OutputDirectory = d["parts"] }, SevenZip, null, default);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Verified);
        string ext = style == SplitStyle.SevenZip ? ".7z" : ".zip";
        Assert.Equal($"Holiday Photos{ext}.001", Path.GetFileName(result.FirstPart));
        Assert.Equal(5, Directory.GetFiles(d["parts"]).Length); // the temporary archive is gone

        var set = Single(PartDetector.Detect([d["parts"]]));
        var extracted = await Combine(set, output: d["out"]);
        Assert.True(extracted.Success, extracted.Error);
        AssertTreesEqual(src, extracted.OutputPath!);
    }

    [Theory]
    [InlineData(SplitStyle.SevenZip)]
    [InlineData(SplitStyle.Zip)]
    public async Task Password_EncryptsTheParts_AndOnlyTheRightOneOpensThem(SplitStyle style)
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Private"));
        // Zip only allows ASCII passwords; 7z gets one with non-ASCII letters too.
        string password = style == SplitStyle.Zip ? "Tr0ub4dor & spaces!" : "Tr0ub4dor & spaces ñ";

        var result = await FileSplitter.SplitAsync(new SplitOptions
        {
            Source = src, Parts = 3, Style = style, OutputDirectory = d["parts"], Password = password,
        }, SevenZip, null, default);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Verified);

        // The file contents must not be readable in the parts.
        var everything = Directory.GetFiles(d["parts"]).SelectMany(File.ReadAllBytes).ToArray();
        Assert.Equal(-1, everything.AsSpan().IndexOf("héllo wörld"u8));

        var set = Single(PartDetector.Detect([d["parts"]]));
        var noPassword = await Combine(set, output: d["out1"]);
        Assert.False(noPassword.Success);
        Assert.True(noPassword.PasswordProblem, noPassword.Error);

        var wrong = await Combine(set, password: "nope", output: d["out2"]);
        Assert.False(wrong.Success);

        var right = await Combine(set, password: password, output: d["out3"]);
        Assert.True(right.Success, right.Error);
        AssertTreesEqual(src, right.OutputPath!);
    }

    [Fact]
    public void Validation_CatchesBadSettings()
    {
        using var d = new TempDir();
        var tiny = d.File("tiny.bin", 10);
        Assert.Contains("too small", FileSplitter.Validate(new SplitOptions { Source = tiny, Parts = 6 }, true));
        Assert.Null(FileSplitter.Validate(new SplitOptions { Source = tiny, Parts = 5 }, true));
        Assert.Contains("at least 2", FileSplitter.Validate(new SplitOptions { Source = tiny, Parts = 1 }, true));
        Assert.Contains("folder can only", FileSplitter.Validate(new SplitOptions { Source = d.Path, Parts = 3 }, true));
        Assert.Contains("7-Zip is needed", FileSplitter.Validate(new SplitOptions { Source = d.Path, Parts = 3, Style = SplitStyle.Zip }, false));
        var big = d.File("big.bin", 100_000);
        Assert.Contains("676", FileSplitter.Validate(new SplitOptions { Source = big, Parts = 677, Style = SplitStyle.Alphabetic }, true));
        Assert.Contains("Choose a file", FileSplitter.Validate(new SplitOptions { Source = d["nope.bin"], Parts = 3 }, true));
        Assert.Contains("password needs", FileSplitter.Validate(new SplitOptions { Source = big, Parts = 3, Password = "x" }, true));
        Assert.Null(FileSplitter.Validate(new SplitOptions { Source = big, Parts = 3, Password = "x", Style = SplitStyle.Zip }, true));
        Assert.Contains("use the 7z style", FileSplitter.Validate(new SplitOptions { Source = big, Parts = 3, Password = "ñ", Style = SplitStyle.Zip }, true));
        Assert.Null(FileSplitter.Validate(new SplitOptions { Source = big, Parts = 3, Password = "ñ", Style = SplitStyle.SevenZip }, true));
    }

    [Fact]
    public async Task ExistingParts_AreNeverOverwritten()
    {
        using var d = new TempDir();
        var source = d.File("data.bin", 9000, 1);
        var existing = d.File("data.bin.002", 5, 9);

        var result = await FileSplitter.SplitAsync(new SplitOptions { Source = source, Parts = 3 }, null, null, default);
        Assert.False(result.Success);
        Assert.Contains("data.bin.002 already exists", result.Error);
        Assert.Equal(5, new FileInfo(existing).Length);
        Assert.False(File.Exists(d["data.bin.001"]));
    }

    [Fact]
    public async Task Cancelling_RemovesThePartsWrittenSoFar()
    {
        using var d = new TempDir();
        var source = d.File("big.bin", RandomBytes(30_000_000, 3));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Fraction > 0.3) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileSplitter.SplitAsync(new SplitOptions { Source = source, Parts = 10, OutputDirectory = d["parts"] }, null, progress, cts.Token));
        Assert.Empty(Directory.GetFiles(d["parts"]));
    }

    private sealed class SyncProgress(Action<SplitProgress> action) : IProgress<SplitProgress>
    {
        public void Report(SplitProgress value) => action(value);
    }
}
