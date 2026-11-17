using System.Diagnostics;
using PartCombiner.Core;

namespace PartCombiner.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests", Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public string File(string name, int size, byte fill = 0x41)
    {
        var full = this[name];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var data = new byte[size];
        Array.Fill(data, fill);
        System.IO.File.WriteAllBytes(full, data);
        return full;
    }

    public string File(string name, byte[] data)
    {
        var full = this[name];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, data);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class TestUtil
{
    public static readonly SevenZipEngine? SevenZip = SevenZipEngine.Locate();

    public static byte[] RandomBytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A file that starts with a valid RAR 5 main archive header for the given (0-based) volume.</summary>
    public static byte[] Rar5Volume(int volume, int size, bool isVolume = true)
    {
        var body = new List<byte> { 0x01, 0x00 }; // type = main archive header, header flags = 0
        ulong archiveFlags = (isVolume ? 1u : 0u) | (volume > 0 ? 2u : 0u);
        body.AddRange(Vint(archiveFlags));
        if (volume > 0) body.AddRange(Vint((ulong)volume));
        var crcData = Vint((ulong)body.Count).Concat(body).ToArray();

        var header = "Rar!\x1A\x07\x01\x00"u8.ToArray()
            .Concat(BitConverter.GetBytes(System.IO.Hashing.Crc32.HashToUInt32(crcData)))
            .Concat(crcData)
            .ToArray();
        var file = new byte[Math.Max(size, header.Length)];
        header.CopyTo(file, 0);
        return file;
    }

    /// <summary>A file that starts with a valid RAR 4 main archive header.</summary>
    public static byte[] Rar4Volume(bool firstVolumeFlag, int size)
    {
        ushort flags = (ushort)(0x0001 | 0x0010 | (firstVolumeFlag ? 0x0100 : 0));
        byte[] block = [0x73, (byte)flags, (byte)(flags >> 8), 13, 0, 0, 0, 0, 0, 0, 0];
        ushort crc = (ushort)(System.IO.Hashing.Crc32.HashToUInt32(block) & 0xFFFF);
        var header = "Rar!\x1A\x07\x00"u8.ToArray().Concat(BitConverter.GetBytes(crc)).Concat(block).ToArray();
        var file = new byte[Math.Max(size, header.Length)];
        header.CopyTo(file, 0);
        return file;
    }

    public static byte[] WithHeader(byte[] header, int size)
    {
        var file = new byte[Math.Max(size, header.Length)];
        header.CopyTo(file, 0);
        return file;
    }

    private static IEnumerable<byte> Vint(ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            yield return value != 0 ? (byte)(b | 0x80) : b;
        } while (value != 0);
    }

    /// <summary>A small folder tree with random content.</summary>
    public static string MakeTree(TempDir dir, string root)
    {
        dir.File(System.IO.Path.Combine(root, "a.bin"), RandomBytes(700_000, 1));
        dir.File(System.IO.Path.Combine(root, "sub", "b.bin"), RandomBytes(400_000, 2));
        dir.File(System.IO.Path.Combine(root, "sub", "ñame with spaces.txt"), "héllo wörld"u8.ToArray());
        return dir[root];
    }

    public static void Run7z(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(SevenZip!.ExecutablePath)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"7z {string.Join(' ', args)} failed: {err}");
    }

    /// <summary>Split a file into byte chunks named by <paramref name="namer"/>(index).</summary>
    public static List<string> SplitFile(string source, int chunkSize, Func<int, string> namer)
    {
        var data = System.IO.File.ReadAllBytes(source);
        var dir = System.IO.Path.GetDirectoryName(source)!;
        var paths = new List<string>();
        for (int i = 0, offset = 0; offset < data.Length; i++, offset += chunkSize)
        {
            var path = System.IO.Path.Combine(dir, namer(i));
            System.IO.File.WriteAllBytes(path, data.AsSpan(offset, Math.Min(chunkSize, data.Length - offset)).ToArray());
            paths.Add(path);
        }
        return paths;
    }

    public static void AssertTreesEqual(string expected, string actual)
    {
        var e = Directory.GetFiles(expected, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(expected, f)).Order().ToList();
        var a = Directory.GetFiles(actual, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(actual, f)).Order().ToList();
        Assert.Equal(e, a);
        foreach (var rel in e)
            Assert.True(System.IO.File.ReadAllBytes(System.IO.Path.Combine(expected, rel))
                .AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(System.IO.Path.Combine(actual, rel))), $"{rel} differs");
    }

    public static Task<CombineResult> Combine(PartSet set, bool extract = true, string? password = null, string? output = null) =>
        new CombineService(SevenZip).CombineAsync(
            set,
            new CombineOptions { ExtractArchives = extract, Password = password, OutputDirectory = output },
            null,
            CancellationToken.None);

    public static PartSet Single(DetectionResult result)
    {
        Assert.Single(result.Sets);
        return result.Sets[0];
    }

    public static string Errors(PartSet set) =>
        string.Join(" | ", set.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));
}
