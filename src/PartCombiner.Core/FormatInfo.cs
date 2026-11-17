using System.Buffers.Binary;
using System.IO.Hashing;

namespace PartCombiner.Core;

/// <summary>Archive format helpers: extensions, magic bytes and header checks.</summary>
public static class FormatInfo
{
    private static readonly (string Ext, ArchiveFormat Format)[] ArchiveExtensions =
    [
        (".tar.gz", ArchiveFormat.TarGz),
        (".tgz", ArchiveFormat.TarGz),
        (".tar.bz2", ArchiveFormat.TarBz2),
        (".tbz2", ArchiveFormat.TarBz2),
        (".tbz", ArchiveFormat.TarBz2),
        (".tar.xz", ArchiveFormat.TarXz),
        (".txz", ArchiveFormat.TarXz),
        (".tar.zst", ArchiveFormat.TarZst),
        (".tzst", ArchiveFormat.TarZst),
        (".tar", ArchiveFormat.Tar),
        (".7z", ArchiveFormat.SevenZip),
        (".rar", ArchiveFormat.Rar),
        (".zip", ArchiveFormat.Zip),
    ];

    /// <summary>Archive format implied by a file name's extension, or Unknown.</summary>
    public static ArchiveFormat FromFileName(string name)
    {
        foreach (var (ext, format) in ArchiveExtensions)
        {
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return format;
        }
        return ArchiveFormat.Unknown;
    }

    /// <summary>"Photos.tar.gz" → "Photos". Names without an archive extension come back unchanged.</summary>
    public static string StripArchiveExtension(string name)
    {
        foreach (var (ext, _) in ArchiveExtensions)
        {
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        }
        return name;
    }

    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Rar => ".rar",
        ArchiveFormat.Zip => ".zip",
        ArchiveFormat.Tar => ".tar",
        ArchiveFormat.TarGz => ".tar.gz",
        ArchiveFormat.TarBz2 => ".tar.bz2",
        ArchiveFormat.TarXz => ".tar.xz",
        ArchiveFormat.TarZst => ".tar.zst",
        _ => "",
    };

    public static string ShortName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => "7Z",
        ArchiveFormat.Rar => "RAR",
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.Tar or ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz or ArchiveFormat.TarZst => "TAR",
        _ => "",
    };

    /// <summary>True for compressed tarballs that 7-Zip unpacks in two steps (.tar.gz → .tar → files).</summary>
    public static bool IsCompressedTar(ArchiveFormat format) =>
        format is ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz or ArchiveFormat.TarZst;

    /// <summary>Identify an archive from its first bytes. Only formats that 7-Zip can unpack directly are reported.</summary>
    public static ArchiveFormat Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
            return ArchiveFormat.SevenZip;
        if (head.StartsWith("Rar!\x1A\x07"u8))
            return ArchiveFormat.Rar;
        if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith("PK\x07\x08"u8) || head.StartsWith("PK\x05\x06"u8))
            return ArchiveFormat.Zip;
        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
            return ArchiveFormat.Tar;
        return ArchiveFormat.Unknown;
    }

    public static byte[] ReadHead(string path, int count = 512)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            var buffer = new byte[(int)Math.Min(fs.Length, count)];
            fs.ReadExactly(buffer);
            return buffer;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// For a 7z archive, the total size the archive must have, read from its (CRC-checked) start header.
    /// Returns null if the header isn't a valid 7z start header.
    /// </summary>
    public static long? SevenZipExpectedSize(ReadOnlySpan<byte> head)
    {
        if (head.Length < 32 || Sniff(head) != ArchiveFormat.SevenZip)
            return null;

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(8, 4));
        if (Crc32.HashToUInt32(head.Slice(12, 20)) != storedCrc)
            return null;

        ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(12, 8));
        ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(20, 8));
        if (nextHeaderOffset > long.MaxValue / 2 || nextHeaderSize > long.MaxValue / 2)
            return null;
        if (nextHeaderSize == 0)
            return null; // empty archive or one still being written

        return 32 + (long)nextHeaderOffset + (long)nextHeaderSize;
    }

    /// <summary>True when the first bytes of an archive look like the given format (false = definitely not it).</summary>
    public static bool StartsLike(ArchiveFormat format, ReadOnlySpan<byte> head) => format switch
    {
        ArchiveFormat.SevenZip => Sniff(head) == ArchiveFormat.SevenZip,
        ArchiveFormat.Rar => Sniff(head) == ArchiveFormat.Rar,
        ArchiveFormat.Zip => Sniff(head) == ArchiveFormat.Zip,
        ArchiveFormat.Tar => head.Length < 262 || Sniff(head) == ArchiveFormat.Tar,
        ArchiveFormat.TarGz => head.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]),
        ArchiveFormat.TarBz2 => head.StartsWith("BZh"u8),
        ArchiveFormat.TarXz => head.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]),
        ArchiveFormat.TarZst => head.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]),
        _ => true,
    };

    /// <summary>What a RAR volume's main header says about itself.</summary>
    public sealed record RarVolumeHeader(int Version, bool IsVolume, int? VolumeNumber, bool ClaimsFirstVolume);

    /// <summary>
    /// Parse the main archive header at the start of a RAR file. Returns null when the file isn't RAR, the
    /// header is encrypted, or its CRC doesn't check out — callers must treat null as "unknown", not as a problem.
    /// </summary>
    public static RarVolumeHeader? ReadRarVolumeHeader(ReadOnlySpan<byte> head)
    {
        // RAR 5: signature, then CRC32 | size vint | type vint (1 = main) | flags vint | [extra size] | archive flags | [volume number]
        if (head.StartsWith("Rar!\x1A\x07\x01\x00"u8))
        {
            int pos = 8;
            if (head.Length < pos + 4) return null;
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(pos, 4));
            pos += 4;
            int crcStart = pos;
            if (!ReadVint(head, ref pos, out ulong headerSize) || headerSize > 4096) return null;
            int end = pos + (int)headerSize;
            if (end > head.Length) return null;
            if (Crc32.HashToUInt32(head[crcStart..end]) != crc) return null;

            if (!ReadVint(head, ref pos, out ulong type) || type != 1) return null; // 4 = encrypted headers
            if (!ReadVint(head, ref pos, out ulong headerFlags)) return null;
            if ((headerFlags & 0x0001) != 0 && !ReadVint(head, ref pos, out _)) return null;
            if ((headerFlags & 0x0002) != 0 && !ReadVint(head, ref pos, out _)) return null;
            if (!ReadVint(head, ref pos, out ulong archiveFlags)) return null;

            bool isVolume = (archiveFlags & 0x0001) != 0;
            int volume = 0;
            if ((archiveFlags & 0x0002) != 0)
            {
                if (!ReadVint(head, ref pos, out ulong v) || v > int.MaxValue) return null;
                volume = (int)v;
            }
            return new RarVolumeHeader(5, isVolume, volume, isVolume && volume == 0);
        }

        // RAR 4: signature, then HEAD_CRC(2) | HEAD_TYPE = 0x73 | HEAD_FLAGS(2) | HEAD_SIZE(2) | reserved(6)
        if (head.StartsWith("Rar!\x1A\x07\x00"u8))
        {
            const int p = 7;
            if (head.Length < p + 13 || head[p + 2] != 0x73) return null;
            int size = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(p + 5, 2));
            if (size < 13 || p + size > head.Length) return null;
            ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(p, 2));
            if ((ushort)(Crc32.HashToUInt32(head.Slice(p + 2, size - 2)) & 0xFFFF) != crc) return null;
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(p + 3, 2));
            // RAR 4 doesn't store the volume number here; MHD_FIRSTVOLUME (0x0100) marks the first volume (RAR 3.0+).
            return new RarVolumeHeader(4, (flags & 0x0001) != 0, null, (flags & 0x0100) != 0);
        }

        return null;
    }

    private static bool ReadVint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && pos < data.Length; shift += 7)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    public enum RarVolumeEnd { Unknown, LastVolume, MoreVolumesFollow }

    /// <summary>
    /// Read the end-of-archive marker of a RAR volume to tell whether more volumes should follow it.
    /// Both RAR 5 and RAR 4 markers are checked with their header CRC, so a mismatch just yields Unknown.
    /// </summary>
    public static RarVolumeEnd ReadRarVolumeEnd(string path)
    {
        byte[] tail;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            int len = (int)Math.Min(fs.Length, 64);
            if (len < 7) return RarVolumeEnd.Unknown;
            fs.Seek(-len, SeekOrigin.End);
            tail = new byte[len];
            fs.ReadExactly(tail);
        }
        catch (IOException) { return RarVolumeEnd.Unknown; }
        catch (UnauthorizedAccessException) { return RarVolumeEnd.Unknown; }

        return ParseRarVolumeEnd(tail);
    }

    internal static RarVolumeEnd ParseRarVolumeEnd(ReadOnlySpan<byte> tail)
    {
        // RAR 5 end-of-archive header: CRC32(4) | size vint = 3 | type = 5 | header flags = 0 | end flags vint.
        // End flag 0x01 means "this is a volume and it is not the last one".
        if (tail.Length >= 8)
        {
            var h = tail[^8..];
            if (h[4] == 0x03 && h[5] == 0x05 && h[6] == 0x00 && (h[7] & 0x80) == 0)
            {
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(h[..4]);
                if (Crc32.HashToUInt32(h[4..]) == crc)
                    return (h[7] & 0x01) != 0 ? RarVolumeEnd.MoreVolumesFollow : RarVolumeEnd.LastVolume;
            }
        }

        // RAR 4 end-of-archive block: HEAD_CRC(2) | HEAD_TYPE = 0x7B | HEAD_FLAGS(2) | HEAD_SIZE(2) | optional fields.
        // HEAD_CRC is the low 16 bits of CRC32 over HEAD_TYPE..end. Flag 0x0001 means "more volumes follow".
        for (int p = tail.Length - 7; p >= 0; p--)
        {
            if (tail[p + 2] != 0x7B) continue;
            int size = BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(p + 5, 2));
            if (size < 7 || p + size != tail.Length) continue;
            ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(p, 2));
            if ((ushort)(Crc32.HashToUInt32(tail.Slice(p + 2, size - 2)) & 0xFFFF) != crc) continue;
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(p + 3, 2));
            return (flags & 0x0001) != 0 ? RarVolumeEnd.MoreVolumesFollow : RarVolumeEnd.LastVolume;
        }

        return RarVolumeEnd.Unknown;
    }

    /// <summary>
    /// For the final .zip of a spanned ZIP set, the total number of parts recorded in its end-of-central-directory
    /// record, or null if it can't be read.
    /// </summary>
    public static int? ZipSpannedPartCount(string zipPath)
    {
        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            int len = (int)Math.Min(fs.Length, 22 + 65535);
            if (len < 22) return null;
            fs.Seek(-len, SeekOrigin.End);
            var buf = new byte[len];
            fs.ReadExactly(buf);
            for (int p = len - 22; p >= 0; p--)
            {
                if (buf[p] == 0x50 && buf[p + 1] == 0x4B && buf[p + 2] == 0x05 && buf[p + 3] == 0x06)
                {
                    int thisDisk = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 4, 2));
                    return thisDisk == 0xFFFF ? null : thisDisk + 1;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Parse a Total Commander style .crc file (filename=…, size=…, crc32=…).</summary>
    public static CrcReference? ReadCrcFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024) return null;

            string? fileName = null;
            long? size = null;
            uint? crc = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                var key = raw[..eq].Trim().ToLowerInvariant();
                var value = raw[(eq + 1)..].Trim();
                switch (key)
                {
                    case "filename":
                        var clean = Path.GetFileName(value);
                        if (clean.Length > 0 && clean.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                            fileName = clean;
                        break;
                    case "size" when long.TryParse(value, out var s):
                        size = s;
                        break;
                    case "crc32" when uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var c):
                        crc = c;
                        break;
                }
            }
            return fileName is null && size is null && crc is null ? null : new CrcReference(path, fileName, size, crc);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
