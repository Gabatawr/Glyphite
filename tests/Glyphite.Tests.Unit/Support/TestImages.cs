using System.Buffers.Binary;

namespace Glyphite.Tests.Unit.Support;

/// <summary>
/// Minimal, structurally-correct images in every supported container. Only <see cref="Png1x1"/> is a
/// fully valid file; the rest carry just enough structure to exercise sniffing and header walking
/// (which is all the loader does — it never decodes pixels).
/// </summary>
public static class TestImages
{
    /// <summary>Real 1×1 PNG, 70 bytes — used where the bytes must be a genuine image.</summary>
    public static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>PNG signature + IHDR length/type + big-endian width and height.</summary>
    public static byte[] Png(int width, int height)
    {
        var b = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        b[11] = 13;                                  // IHDR chunk length
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), height);
        return b;
    }

    /// <summary>GIF87a/89a header with the logical screen descriptor carrying little-endian size.</summary>
    public static byte[] Gif(int width, int height)
    {
        var b = new byte[13];
        "GIF89a"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)height);
        b[12] = 0x3B;                                // trailer
        return b;
    }

    /// <summary>SOI + APP0 + SOF0 (height then width, big-endian) + EOI.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        var b = new byte[41];
        b[0] = 0xFF; b[1] = 0xD8;                    // SOI
        b[2] = 0xFF; b[3] = 0xE0;                    // APP0
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 16);
        "JFIF\0"u8.CopyTo(b.AsSpan(6));
        b[11] = 1; b[12] = 1;
        b[15] = 1; b[17] = 1;
        b[20] = 0xFF; b[21] = 0xC0;                  // SOF0
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), 17);
        b[24] = 8;                                   // sample precision
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(25), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(27), (ushort)width);
        b[39] = 0xFF; b[40] = 0xD9;                  // EOI
        return b;
    }

    /// <summary>RIFF/WEBP with a lossless (VP8L) chunk — size packed as two 14-bit fields.</summary>
    public static byte[] WebpLossless(int width, int height)
    {
        var b = RiffHeader("VP8L", 5);
        b[20] = 0x2F;                                // signature byte required by the format
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(21),
            (uint)((width - 1) | ((height - 1) << 14)));
        return b;
    }

    /// <summary>RIFF/WEBP with an extended (VP8X) chunk — 24-bit canvas size minus one.</summary>
    public static byte[] WebpExtended(int width, int height)
    {
        var b = RiffHeader("VP8X", 10);
        WriteUInt24LittleEndian(b.AsSpan(24), width - 1);
        WriteUInt24LittleEndian(b.AsSpan(27), height - 1);
        return b;
    }

    /// <summary>RIFF/WEBP with a lossy (VP8 ) chunk — start code, then two 14-bit dimensions.</summary>
    public static byte[] WebpLossy(int width, int height)
    {
        var b = RiffHeader("VP8 ", 10);
        b[23] = 0x9D; b[24] = 0x01; b[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), (ushort)(width & 0x3FFF));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)(height & 0x3FFF));
        return b;
    }

    private static byte[] RiffHeader(string fourCc, uint chunkSize)
    {
        var b = new byte[30];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 8));
        "WEBP"u8.CopyTo(b.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(b, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), chunkSize);
        return b;
    }

    private static void WriteUInt24LittleEndian(Span<byte> target, int value)
    {
        target[0] = (byte)(value & 0xFF);
        target[1] = (byte)((value >> 8) & 0xFF);
        target[2] = (byte)((value >> 16) & 0xFF);
    }
}

/// <summary>Throwaway directory for tests that need real files on disk.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("glyphite-img-").FullName;

    public string Write(string name, byte[] bytes)
    {
        var full = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public string Write(string name, string text)
    {
        var full = System.IO.Path.Combine(Path, name);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best effort */ }
    }
}
