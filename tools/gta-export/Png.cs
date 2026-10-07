using System.Buffers.Binary;
using System.IO.Compression;

namespace GtaExport;

/// <summary>Minimal 8-bit RGB PNG writer (no external dependencies).</summary>
public static class Png
{
    static readonly uint[] CrcTable = MakeTable();

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> hdr = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)data.Length);
        s.Write(hdr);
        Span<byte> t = stackalloc byte[4] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        s.Write(t);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(hdr, Crc(t, data));
        s.Write(hdr);
    }

    /// <param name="rgb">width*height*3 bytes, row 0 at the top</param>
    public static void WriteRgb(string path, int width, int height, byte[] rgb)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
        {
            var row = new byte[width * 3 + 1];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0;
                Buffer.BlockCopy(rgb, y * width * 3, row, 1, width * 3);
                z.Write(row, 0, row.Length);
            }
        }
        Paths.AtomicWrite(path, s =>
        {
            s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4), (uint)height);
            ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            Chunk(s, "IHDR", ihdr);
            Chunk(s, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));
            Chunk(s, "IEND", ReadOnlySpan<byte>.Empty);
        });
    }
}
