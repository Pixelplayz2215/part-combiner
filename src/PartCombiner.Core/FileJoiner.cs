using System.IO.Hashing;

namespace PartCombiner.Core;

/// <summary>Concatenates part files into one file, fast and cancellable.</summary>
public static class FileJoiner
{
    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// Write all parts, in order, into <paramref name="destination"/> (which must not exist yet).
    /// Reading the next chunk overlaps with writing the previous one.
    /// </summary>
    /// <returns>The CRC32 of the joined data when <paramref name="computeCrc"/> is set.</returns>
    public static async Task<uint?> JoinAsync(
        IReadOnlyList<string> parts,
        string destination,
        bool computeCrc,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        long total = 0;
        foreach (var part in parts)
            total += new FileInfo(part).Length;

        var crc = computeCrc ? new Crc32() : null;
        var bufA = new byte[BufferSize];
        var bufB = new byte[BufferSize];
        long written = 0;

        var outOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            PreallocationSize = total,
        };

        await using (var output = new FileStream(destination, outOptions))
        {
            foreach (var part in parts)
            {
                var inOptions = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 0,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                };
                await using var input = new FileStream(part, inOptions);

                int read = await input.ReadAsync(bufA, ct).ConfigureAwait(false);
                while (read > 0)
                {
                    var chunk = new ReadOnlyMemory<byte>(bufA, 0, read);
                    var write = output.WriteAsync(chunk, ct);
                    crc?.Append(chunk.Span);
                    int next = await input.ReadAsync(bufB, ct).ConfigureAwait(false);
                    await write.ConfigureAwait(false);

                    written += read;
                    progress?.Report(written);

                    (bufA, bufB) = (bufB, bufA);
                    read = next;
                }
            }

            // Preallocation reserves space; trim in case a part shrank while we were reading.
            if (output.Length != written)
                output.SetLength(written);
        }

        return crc?.GetCurrentHashAsUInt32();
    }
}
