using PartCombiner.Core;
using static PartCombiner.Tests.TestUtil;

namespace PartCombiner.Tests;

/// <summary>The "will it fit" check that runs before 7-Zip extracts anything.</summary>
public class FreeSpaceTests
{
    [Fact]
    public void ListingParser_SumsEntrySizes_AndIgnoresArchiveProperties()
    {
        var parser = new SevenZipEngine.ListingSizeParser();
        foreach (var line in """
            Listing archive: arc.7z.001

            --
            Path = arc.7z.001
            Type = Split
            Physical Size = 4096
            ----
            Path = arc.7z
            Size = 8189
            --
            Path = arc.7z
            Type = 7z
            Physical Size = 8189

            ----------
            Path = Movie
            Size = 0
            Packed Size = 0

            Path = Movie\a.txt
            Size = 5002
            Packed Size = 5002

            Path = Movie\sub\b.txt
            Size = 3002
            Packed Size = 3002
            """.Split('\n'))
        {
            parser.Add(line.TrimEnd('\r'));
        }
        Assert.Equal(8004, parser.Result);
    }

    [Fact]
    public void ListingParser_UnknownSize_GivesNoAnswer()
    {
        var parser = new SevenZipEngine.ListingSizeParser();
        foreach (var line in new[] { "----------", "Path = data.tar", "Size = ", "Packed Size = 1234" })
            parser.Add(line);
        Assert.Null(parser.Result);
    }

    [Fact]
    public void ListingParser_NoEntries_GivesNoAnswer()
    {
        var parser = new SevenZipEngine.ListingSizeParser();
        foreach (var line in new[] { "--", "Path = x.7z", "Size = 99" })
            parser.Add(line);
        Assert.Null(parser.Result);
    }

    [Fact]
    public async Task UnpackedSize_OfASplitArchive_IsTheSumOfItsFiles()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Data"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx1", "data.7z", src);

        long expected = Directory.GetFiles(src, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.Equal(expected, await SevenZip.GetUnpackedSizeAsync(d[Path.Combine("parts", "data.7z.001")], null, default));
    }

    [Fact]
    public async Task ArchiveThatUnpacksLargerThanTheFreeSpace_FailsUpFront_AndSavesNothing()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        // Very compressible: the parts are tiny, the contents are 6 MB. The old check (part sizes) would have passed.
        d.File(Path.Combine("src", "Logs", "a.log"), 4_000_000, (byte)'a');
        d.File(Path.Combine("src", "Logs", "b.log"), 2_000_000, (byte)'b');
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v20k", "-mx9", "logs.7z", d[Path.Combine("src", "Logs")]);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.True(set.TotalSize < 100_000);
        Directory.CreateDirectory(d["out"]);

        var tight = await CombineWithFreeSpace(set, d["out"], free: 6_000_000 - 1);
        Assert.False(tight.Success);
        Assert.Contains("Not enough free space on T:", tight.Error);
        Assert.Contains($"needs {Bytes.Format(6_000_000)}", tight.Error);
        Assert.Empty(Directory.GetFileSystemEntries(d["out"], "*", new EnumerationOptions { AttributesToSkip = 0 }));

        var enough = await CombineWithFreeSpace(set, d["out"], free: 6_000_000);
        Assert.True(enough.Success, enough.Error);
        AssertTreesEqual(d[Path.Combine("src", "Logs")], enough.OutputPath!);
    }

    [Fact]
    public async Task PartNamesThatNeedJoiningFirst_AreCheckedAfterTheJoin()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        d.File(Path.Combine("src", "Notes", "n.txt"), 3_000_000, (byte)'n');
        Directory.CreateDirectory(d["work"]);
        Run7z(d["work"], "a", "-mx9", "notes.7z", d[Path.Combine("src", "Notes")]);
        SplitFile(d[Path.Combine("work", "notes.7z")], 2_000, i => $"notes.7z.part{i + 1}");
        File.Delete(d[Path.Combine("work", "notes.7z")]);

        var set = Single(PartDetector.Detect([d["work"]]));
        Assert.False(set.IsBlocked, Errors(set));

        // Room for the temporary joined archive, not for the 3 MB it unpacks to.
        var result = await CombineWithFreeSpace(set, d["out"], free: 1_000_000);
        Assert.False(result.Success);
        Assert.Contains("Not enough free space", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(d["out"], "*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public async Task EncryptedHeaders_WithoutPassword_StillReportThePasswordProblem()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Secret"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx1", "-pHunter2", "-mhe=on", "secret.7z", src);

        // The size can't be read without the password, so the space check steps aside instead of guessing.
        var set = Single(PartDetector.Detect([d["parts"]]));
        var result = await CombineWithFreeSpace(set, d["out"], free: 10);
        Assert.False(result.Success);
        Assert.True(result.PasswordProblem, result.Error);
    }

    private static Task<CombineResult> CombineWithFreeSpace(PartSet set, string output, long free) =>
        new CombineService(SevenZip) { FreeSpace = _ => ("T:", free) }.CombineAsync(
            set, new CombineOptions { OutputDirectory = output }, null, CancellationToken.None);
}
