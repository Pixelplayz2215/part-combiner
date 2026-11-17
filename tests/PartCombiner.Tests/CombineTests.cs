using System.IO.Hashing;
using System.Text;
using PartCombiner.Core;
using static PartCombiner.Tests.TestUtil;

namespace PartCombiner.Tests;

/// <summary>End-to-end tests against the installed 7-Zip. They return early (pass) when 7-Zip isn't installed.</summary>
public class CombineTests
{
    [Fact]
    public void SevenZip_IsFound_WhenInstalled()
    {
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        if (File.Exists(standard))
            Assert.NotNull(SevenZip);
    }

    [Fact]
    public async Task SplitSevenZip_ExtractsTheOriginalFolder()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "My Folder"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx1", "photos.7z", src);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.Equal(SetKind.SplitArchive, set.Kind);
        Assert.Equal(ArchiveFormat.SevenZip, set.Format);
        Assert.False(set.IsBlocked, Errors(set));

        var result = await Combine(set, output: d["out"]);
        Assert.True(result.Success, result.Error);
        Assert.Equal(d[Path.Combine("out", "My Folder")], result.OutputPath);
        AssertTreesEqual(src, result.OutputPath!);
        Assert.Empty(Directory.GetDirectories(d["out"], ".pc-*"));
    }

    [Fact]
    public async Task SplitSevenZip_MissingLastPart_IsDetectedBeforeExtracting()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Data"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx1", "data.7z", src);
        var last = Directory.GetFiles(d["parts"]).Order().Last();
        File.Delete(last);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.True(set.IsBlocked);
        Assert.Contains("Incomplete", Errors(set));
    }

    [Fact]
    public async Task SplitSevenZip_JoinOnly_RebuildsTheArchive()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Data"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx1", "data.7z", src);

        var set = Single(PartDetector.Detect([d["parts"]]));
        var result = await Combine(set, extract: false);
        Assert.True(result.Success, result.Error);
        Assert.Equal(d[Path.Combine("parts", "data.7z")], result.OutputPath);

        var expected = set.Parts.SelectMany(p => File.ReadAllBytes(p.Path)).ToArray();
        Assert.True(File.ReadAllBytes(result.OutputPath!).AsSpan().SequenceEqual(expected));
        Run7z(d["parts"], "t", result.OutputPath!); // the rebuilt archive is valid
    }

    [Fact]
    public async Task ArchiveWithManyTopLevelItems_GetsItsOwnFolder()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        MakeTree(d, "src");
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["src"], "a", "-v300k", "-mx1", d[Path.Combine("parts", "Loose Files.7z")], "a.bin", "sub");

        var set = Single(PartDetector.Detect([d["parts"]]));
        var result = await Combine(set);
        Assert.True(result.Success, result.Error);
        Assert.Equal(d[Path.Combine("parts", "Loose Files")], result.OutputPath);
        AssertTreesEqual(d["src"], result.OutputPath!);
        Assert.False(new DirectoryInfo(result.OutputPath!).Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task ExistingOutput_IsNotOverwritten()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Data"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx1", "data.7z", src);
        d.File(Path.Combine("parts", "Data", "keep.txt"), 5);

        var set = Single(PartDetector.Detect([d["parts"]]));
        var result = await Combine(set);
        Assert.True(result.Success, result.Error);
        Assert.Equal(d[Path.Combine("parts", "Data (2)")], result.OutputPath);
        Assert.True(File.Exists(d[Path.Combine("parts", "Data", "keep.txt")]));
    }

    [Fact]
    public async Task SplitZip_Extracts()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Zipped"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-tzip", "-v300k", "-mx1", "zipped.zip", src);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.Equal(ArchiveFormat.Zip, set.Format);
        var result = await Combine(set);
        Assert.True(result.Success, result.Error);
        AssertTreesEqual(src, result.OutputPath!);
    }

    [Fact]
    public async Task EncryptedArchive_NeedsThePassword()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Secret"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx1", "-pP@ss wörd", "-mhe=on", "secret.7z", src);

        var set = Single(PartDetector.Detect([d["parts"]]));

        var noPassword = await Combine(set);
        Assert.False(noPassword.Success);
        Assert.True(noPassword.PasswordProblem);

        var wrong = await Combine(set, password: "nope");
        Assert.True(wrong.PasswordProblem);

        var right = await Combine(set, password: "P@ss wörd");
        Assert.True(right.Success, right.Error);
        AssertTreesEqual(src, right.OutputPath!);
        Assert.Empty(Directory.GetDirectories(d["parts"], ".pc-*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public async Task PartSuffixSplitArchive_IsJoinedThenExtracted()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Stuff"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-mx1", "stuff.7z", src);
        var archive = d[Path.Combine("parts", "stuff.7z")];
        SplitFile(archive, 250_000, i => $"stuff.7z.part{i + 1}");
        File.Delete(archive);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.Equal(NamingScheme.PartSuffix, set.Scheme);
        var result = await Combine(set);
        Assert.True(result.Success, result.Error);
        AssertTreesEqual(src, result.OutputPath!);
        Assert.Empty(Directory.GetFiles(d["parts"], ".pc-*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public async Task SplitTarGz_IsUnpackedAllTheWay()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Tarred"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["src"], "a", "-ttar", d[Path.Combine("parts", "bundle.tar")], "Tarred");
        Run7z(d["parts"], "a", "-tgzip", "bundle.tar.gz", "bundle.tar");
        File.Delete(d[Path.Combine("parts", "bundle.tar")]);
        SplitFile(d[Path.Combine("parts", "bundle.tar.gz")], 300_000, i => $"bundle.tar.gz.{i + 1:000}");
        File.Delete(d[Path.Combine("parts", "bundle.tar.gz")]);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.Equal(ArchiveFormat.TarGz, set.Format);
        var result = await Combine(set);
        Assert.True(result.Success, result.Error);
        AssertTreesEqual(src, result.OutputPath!);
    }

    [Fact]
    public async Task SpannedZip_z01_Extracts()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        Directory.CreateDirectory(d["parts"]);
        var files = new Dictionary<string, byte[]>
        {
            ["Spanned/first.bin"] = RandomBytes(150_000, 21),
            ["Spanned/second.bin"] = RandomBytes(150_000, 22),
        };
        SpannedZipWriter.Write(d["parts"], "spanned", files, segmentSize: 100_000);
        Assert.Equal(["spanned.z01", "spanned.z02", "spanned.z03", "spanned.zip"],
            Directory.GetFiles(d["parts"]).Select(Path.GetFileName).Order());
        Assert.All(new[] { "z01", "z02", "z03" }, ext =>
            Assert.Equal(100_000, new FileInfo(d[Path.Combine("parts", "spanned." + ext)]).Length));

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.Equal(SetKind.ZipSpanned, set.Kind);
        Assert.False(set.IsBlocked, Errors(set));

        var result = await Combine(set);
        Assert.True(result.Success, result.Error);
        foreach (var (name, data) in files)
            Assert.True(File.ReadAllBytes(Path.Combine(d["parts"], name)).AsSpan().SequenceEqual(data), name);

        // A missing middle part is caught by name; the .zip's own record catches a missing trailing .z part.
        File.Delete(d[Path.Combine("parts", "spanned.z03")]);
        set = Single(PartDetector.Detect([d["parts"]]));
        Assert.Contains("Expected 4 parts", Errors(set));
    }

    [Fact]
    public async Task RawSplit_JoinsByteForByte_AndChecksTheCrcFile()
    {
        using var d = new TempDir();
        var original = RandomBytes(1_234_567, 5);
        var path = d.File("video.mp4", original);
        SplitFile(path, 400_000, i => $"video.{i + 1:000}");
        File.Delete(path);
        File.WriteAllText(d["video.crc"],
            $"filename=video.mp4\r\nsize={original.Length}\r\ncrc32={Crc32.HashToUInt32(original):X8}\r\n");

        var set = Single(PartDetector.Detect([d.Path]));
        Assert.Equal(SetKind.SplitFile, set.Kind);
        var result = await Combine(set);
        Assert.True(result.Success, result.Error);
        Assert.True(result.ChecksumVerified);
        Assert.Equal(d["video.mp4"], result.OutputPath);
        Assert.True(File.ReadAllBytes(result.OutputPath!).AsSpan().SequenceEqual(original));

        // Corrupt one byte in a part: the checksum must catch it.
        File.Delete(result.OutputPath!);
        var part2 = d["video.002"];
        var bytes = File.ReadAllBytes(part2);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(part2, bytes);
        set = Single(PartDetector.Detect([d.Path]));
        var bad = await Combine(set);
        Assert.False(bad.Success);
        Assert.Contains("Checksum mismatch", bad.Error);
    }

    [Fact]
    public async Task SwappedPartFromAnotherArchive_IsNotCombined_AndNothingIsSaved()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var a = MakeTree(d, Path.Combine("a", "Data"));
        d.File(Path.Combine("b", "Data", "a.bin"), RandomBytes(700_000, 99)); // same layout, different bytes
        d.File(Path.Combine("b", "Data", "sub", "b.bin"), RandomBytes(400_000, 98));
        Directory.CreateDirectory(d["pa"]);
        Directory.CreateDirectory(d["pb"]);
        Run7z(d["pa"], "a", "-v300k", "-mx0", "data.7z", a);
        Run7z(d["pb"], "a", "-v300k", "-mx0", "data.7z", d[Path.Combine("b", "Data")]);
        File.Copy(d[Path.Combine("pb", "data.7z.002")], d[Path.Combine("pa", "data.7z.002")], overwrite: true);

        var set = Single(PartDetector.Detect([d["pa"]]));
        var result = await Combine(set, output: d["out"]);
        Assert.False(result.Success);
        Assert.Contains("nothing was saved", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(d["out"], "*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public void ExtraPartFromAnotherArchive_IsBlocked()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        var src = MakeTree(d, Path.Combine("src", "Data"));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v300k", "-mx0", "data.7z", src);
        var last = Directory.GetFiles(d["parts"]).Order().Last();
        int n = int.Parse(Path.GetExtension(last).TrimStart('.'));
        File.WriteAllBytes(Path.Combine(d["parts"], $"data.7z.{n + 1:000}"), RandomBytes(1000, 4));
        // Pad the old last part so sizes still look consistent — only the archive header can tell.
        using (var fs = new FileStream(last, FileMode.Open)) fs.SetLength(300 * 1024);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.Contains("more than this archive needs", Errors(set));
    }

    [Fact]
    public async Task PartChangedAfterAdding_IsNotCombined()
    {
        using var d = new TempDir();
        var path = d.File("clip.mov", RandomBytes(300_000, 3));
        SplitFile(path, 100_000, i => $"clip.mov.{i + 1:000}");
        File.Delete(path);

        var set = Single(PartDetector.Detect([d.Path]));
        File.WriteAllBytes(d["clip.mov.002"], RandomBytes(90_000, 4));

        var result = await Combine(set);
        Assert.False(result.Success);
        Assert.Contains("changed since it was added", result.Error);
        Assert.False(File.Exists(d["clip.mov"]));
    }

    [Fact]
    public async Task OverAThousandVolumes_ExtractEndToEnd()
    {
        if (SevenZip is null) return;
        using var d = new TempDir();
        d.File(Path.Combine("src", "Big", "blob.bin"), RandomBytes(1_150_000, 12));
        Directory.CreateDirectory(d["parts"]);
        Run7z(d["parts"], "a", "-v1k", "-mx0", "big.7z", d[Path.Combine("src", "Big")]);

        var set = Single(PartDetector.Detect([d["parts"]]));
        Assert.True(set.Parts.Count > 1000, $"only {set.Parts.Count} parts");
        Assert.Empty(set.Issues);

        var result = await Combine(set, output: d["out"]);
        Assert.True(result.Success, result.Error);
        AssertTreesEqual(d[Path.Combine("src", "Big")], result.OutputPath!);

        // Byte-joining that many parts works too.
        var joined = await Combine(set, extract: false, output: d["joined"]);
        Assert.True(joined.Success, joined.Error);
        Assert.Equal(set.TotalSize, new FileInfo(joined.OutputPath!).Length);
    }

    [Fact]
    public async Task Cancelling_LeavesNoTempFiles()
    {
        using var d = new TempDir();
        var original = RandomBytes(40_000_000, 9);
        var path = d.File("big.bin", original);
        SplitFile(path, 10_000_000, i => $"big.bin.{i + 1:000}");
        File.Delete(path);

        var set = Single(PartDetector.Detect([d.Path]));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<CombineProgress>(p => { if (p.Fraction > 0.1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CombineService(SevenZip).CombineAsync(set, new CombineOptions(), progress, cts.Token));

        Assert.Equal(4, Directory.GetFiles(d.Path).Length); // only the parts remain
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

/// <summary>Writes an Info-ZIP style spanned archive (name.z01, name.z02 … name.zip) with stored entries.</summary>
internal static class SpannedZipWriter
{
    public static void Write(string dir, string baseName, IReadOnlyDictionary<string, byte[]> files, int segmentSize)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(0x08074B50u); // spanning signature

        var entries = new List<(string Name, byte[] Data, uint Crc, long HeaderAt, long DataAt)>();
        foreach (var (name, data) in files)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            uint crc = Crc32.HashToUInt32(data);
            long headerAt = stream.Position;
            w.Write(0x04034B50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0x21); w.Write(crc);
            w.Write((uint)data.Length); w.Write((uint)data.Length);
            w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write(nameBytes);
            long dataAt = stream.Position;
            w.Write(data);
            entries.Add((name, data, crc, headerAt, dataAt));
        }

        long cdStart = stream.Position;
        // Fixed-size segments, like real splitters make. Every cut must land inside file data, never in a header.
        var bounds = new List<long>();
        for (long b = segmentSize; b < cdStart; b += segmentSize)
        {
            if (!entries.Any(e => b > e.DataAt && b < e.DataAt + e.Data.Length))
                throw new InvalidOperationException($"Segment boundary {b} would split a header; pick other sizes");
            bounds.Add(b);
        }
        (int Disk, long Offset) Locate(long at)
        {
            int disk = bounds.Count(b => b <= at);
            long start = disk == 0 ? 0 : bounds[disk - 1];
            return (disk, at - start);
        }

        foreach (var e in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(e.Name);
            var (disk, offset) = Locate(e.HeaderAt);
            w.Write(0x02014B50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0x21); w.Write(e.Crc);
            w.Write((uint)e.Data.Length); w.Write((uint)e.Data.Length);
            w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)disk); w.Write((ushort)0); w.Write(0u); w.Write((uint)offset);
            w.Write(nameBytes);
        }
        long cdSize = stream.Position - cdStart;
        int lastDisk = bounds.Count;
        var (cdDisk, cdOffset) = Locate(cdStart);
        w.Write(0x06054B50u); w.Write((ushort)lastDisk); w.Write((ushort)cdDisk);
        w.Write((ushort)entries.Count); w.Write((ushort)entries.Count);
        w.Write((uint)cdSize); w.Write((uint)cdOffset); w.Write((ushort)0);
        w.Flush();

        var all = stream.ToArray();
        long prev = 0;
        for (int i = 0; i <= bounds.Count; i++)
        {
            long end = i < bounds.Count ? bounds[i] : all.Length;
            var name = i < bounds.Count ? $"{baseName}.z{i + 1:00}" : $"{baseName}.zip";
            File.WriteAllBytes(Path.Combine(dir, name), all.AsSpan((int)prev, (int)(end - prev)).ToArray());
            prev = end;
        }
    }
}
