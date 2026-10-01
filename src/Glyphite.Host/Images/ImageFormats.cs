using System.Text;

namespace Glyphite.Host.Images;

/// <summary>
/// Container sniffing for the four formats the provider accepts (JPEG, PNG, GIF, WebP).
/// The provider detects the format from the actual bytes rather than the name or declared
/// MIME type, so everything here does the same — a <c>.txt</c> holding a PNG is a PNG.
/// </summary>
public static class ImageFormats
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";

    public static readonly string[] Supported = [Png, Jpeg, Gif, Webp];

    /// <summary>Minimum bytes needed to identify any supported container.</summary>
    public const int SniffLength = 12;

    /// <summary>Detect the container from the leading bytes; null when they match no supported format.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> head)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (head.Length >= 8
            && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            return Png;

        // JPEG: FF D8 FF
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return Jpeg;

        // GIF: "GIF87a" / "GIF89a"
        if (head.Length >= 6
            && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38
            && (head[4] == 0x37 || head[4] == 0x39) && head[5] == 0x61)
            return Gif;

        // WebP: "RIFF" .... "WEBP"
        if (head.Length >= 12
            && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
            return Webp;

        return null;
    }

    /// <summary>
    /// Pixel dimensions read from the container header, or null when the header is truncated
    /// or uses a variant this reader does not walk. Used to warn about provider dimension limits.
    /// </summary>
    public static (int Width, int Height)? ReadDimensions(ReadOnlySpan<byte> data, string mediaType) => mediaType switch
    {
        Png => PngDimensions(data),
        Jpeg => JpegDimensions(data),
        Gif => GifDimensions(data),
        Webp => WebpDimensions(data),
        _ => null
    };

    /// <summary>IHDR always sits at offset 8; width/height are big-endian at 16 and 20.</summary>
    private static (int, int)? PngDimensions(ReadOnlySpan<byte> d)
    {
        if (d.Length < 24) return null;
        // Only the IHDR chunk carries the real size — refuse anything else masquerading as PNG.
        if (!(d[12] == 0x49 && d[13] == 0x48 && d[14] == 0x44 && d[15] == 0x52)) return null;

        var w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
        var h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }

    /// <summary>Logical screen descriptor: little-endian width/height at 6 and 8.</summary>
    private static (int, int)? GifDimensions(ReadOnlySpan<byte> d)
    {
        if (d.Length < 10) return null;
        var w = d[6] | (d[7] << 8);
        var h = d[8] | (d[9] << 8);
        return w > 0 && h > 0 ? (w, h) : null;
    }

    /// <summary>Walk JPEG segments to the first SOF marker, which carries height then width.</summary>
    private static (int, int)? JpegDimensions(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 <= d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker == 0xFF || marker == 0x00) { i++; continue; }      // padding / stuffed byte
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9)) { i += 2; continue; } // standalone

            var len = (d[i + 2] << 8) | d[i + 3];
            if (len < 2) return null;

            // SOF0-SOF15 except DHT (C4), JPG (C8), DAC (CC)
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);

            i += 2 + len;
        }
        return null;
    }

    /// <summary>Three WebP flavours — lossy (VP8), lossless (VP8L) and extended (VP8X).</summary>
    private static (int, int)? WebpDimensions(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return null;

        if (IsTag(d, 12, "VP8 "))
        {
            // 3-byte tag, 3-byte start code, then 14-bit width and height
            if (!(d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)) return null;
            return (((d[27] & 0x3F) << 8) | d[26], ((d[29] & 0x3F) << 8) | d[28]);
        }

        if (IsTag(d, 12, "VP8L"))
        {
            if (d[20] != 0x2F) return null;
            var bits = d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24);
            return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        if (IsTag(d, 12, "VP8X"))
        {
            var w = (d[24] | (d[25] << 8) | (d[26] << 16)) + 1;
            var h = (d[27] | (d[28] << 8) | (d[29] << 16)) + 1;
            return (w, h);
        }

        return null;
    }

    private static bool IsTag(ReadOnlySpan<byte> d, int offset, string tag)
    {
        if (offset + 4 > d.Length) return false;
        for (var i = 0; i < 4; i++)
            if (d[offset + i] != (byte)tag[i]) return false;
        return true;
    }

    /// <summary>Enough header to reach the dimensions of every supported container.</summary>
    private const int HeaderScanLimit = 128 * 1024;

    /// <summary>
    /// Describe a file as an image — media type, pixel size, byte size — or null when it is not one
    /// of the supported containers. Reads only the header, never the whole picture.
    /// </summary>
    public static string? DescribeImageFile(string path)
    {
        var mediaType = TrySniffFile(path);
        if (mediaType is null) return null;

        try
        {
            var length = new FileInfo(path).Length;
            var head = new byte[Math.Min(length, HeaderScanLimit)];

            using var stream = File.OpenRead(path);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            var dimensions = ReadDimensions(head.AsSpan(0, read), mediaType);
            var shape = dimensions is { } d ? $"{d.Width}×{d.Height}, " : "";
            return $"{mediaType}, {shape}{DescribeBytes(length)}";
        }
        catch
        {
            return mediaType;
        }
    }

    /// <summary>Human-readable byte size for tool output ("900 B", "412 KB", "1.2 MB").</summary>
    public static string DescribeBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    /// <summary>
    /// Sniff a file's container from its first bytes without loading the whole file.
    /// Returns null when the file is missing, unreadable, or not a supported image.
    /// </summary>
    public static string? TrySniffFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[SniffLength];
            var read = stream.ReadAtLeast(head, SniffLength, throwOnEndOfStream: false);
            return SniffMediaType(head[..read]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Guess a media type from a file extension — used only to label URL passthrough parts.</summary>
    public static string? MediaTypeFromExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".png" => Png,
            ".jpg" or ".jpeg" or ".jpe" => Jpeg,
            ".gif" => Gif,
            ".webp" => Webp,
            _ => null
        };
    }

    internal static Encoding TagEncoding { get; } = Encoding.ASCII;
}
