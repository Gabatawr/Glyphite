using System.Text;
using Glyphite.Host.Images;
using Glyphite.Tests.Unit.Support;
using Xunit;

namespace Glyphite.Tests.Unit.Images;

public class ImageFormatsTests
{
    [Fact]
    public void SniffMediaType_RecognisesEverySupportedContainer()
    {
        Assert.Equal(ImageFormats.Png, ImageFormats.SniffMediaType(TestImages.Png1x1));
        Assert.Equal(ImageFormats.Png, ImageFormats.SniffMediaType(TestImages.Png(4, 4)));
        Assert.Equal(ImageFormats.Jpeg, ImageFormats.SniffMediaType(TestImages.Jpeg(4, 4)));
        Assert.Equal(ImageFormats.Gif, ImageFormats.SniffMediaType(TestImages.Gif(4, 4)));
        Assert.Equal(ImageFormats.Webp, ImageFormats.SniffMediaType(TestImages.WebpLossless(4, 4)));
        Assert.Equal(ImageFormats.Webp, ImageFormats.SniffMediaType(TestImages.WebpExtended(4, 4)));
        Assert.Equal(ImageFormats.Webp, ImageFormats.SniffMediaType(TestImages.WebpLossy(4, 4)));
    }

    [Theory]
    [InlineData("plain text, definitely not an image")]
    [InlineData("")]
    [InlineData("GIF88a")]
    [InlineData("RIFF....WAVE")]
    public void SniffMediaType_RejectsEverythingElse(string content)
        => Assert.Null(ImageFormats.SniffMediaType(Encoding.ASCII.GetBytes(content)));

    [Fact]
    public void SniffMediaType_ToleratesShortBuffers()
    {
        Assert.Null(ImageFormats.SniffMediaType([0x89, 0x50]));
        Assert.Null(ImageFormats.SniffMediaType([]));
        Assert.Equal(ImageFormats.Jpeg, ImageFormats.SniffMediaType([0xFF, 0xD8, 0xFF]));
    }

    [Fact]
    public void SniffMediaType_IgnoresTheFileName_OnlyContentCounts()
    {
        // A PNG saved as .txt is still a PNG — the provider detects format from bytes and so do we.
        Assert.Equal(ImageFormats.Png, ImageFormats.SniffMediaType(TestImages.Png1x1));
    }

    [Fact]
    public void ReadDimensions_Png()
        => Assert.Equal((1920, 1080), ImageFormats.ReadDimensions(TestImages.Png(1920, 1080), ImageFormats.Png));

    [Fact]
    public void ReadDimensions_Png1x1()
        => Assert.Equal((1, 1), ImageFormats.ReadDimensions(TestImages.Png1x1, ImageFormats.Png));

    [Fact]
    public void ReadDimensions_Jpeg()
        => Assert.Equal((640, 480), ImageFormats.ReadDimensions(TestImages.Jpeg(640, 480), ImageFormats.Jpeg));

    [Fact]
    public void ReadDimensions_Gif()
        => Assert.Equal((32, 16), ImageFormats.ReadDimensions(TestImages.Gif(32, 16), ImageFormats.Gif));

    [Fact]
    public void ReadDimensions_WebpVariants()
    {
        Assert.Equal((800, 600), ImageFormats.ReadDimensions(TestImages.WebpLossless(800, 600), ImageFormats.Webp));
        Assert.Equal((1024, 768), ImageFormats.ReadDimensions(TestImages.WebpExtended(1024, 768), ImageFormats.Webp));
        Assert.Equal((320, 240), ImageFormats.ReadDimensions(TestImages.WebpLossy(320, 240), ImageFormats.Webp));
    }

    [Fact]
    public void ReadDimensions_ReturnsNull_WhenHeaderIsTruncated()
    {
        Assert.Null(ImageFormats.ReadDimensions(TestImages.Png(10, 10).AsSpan(0, 20), ImageFormats.Png));
        Assert.Null(ImageFormats.ReadDimensions(TestImages.Gif(10, 10).AsSpan(0, 8), ImageFormats.Gif));
        Assert.Null(ImageFormats.ReadDimensions([0x89, 0x50, 0x4E, 0x47], ImageFormats.Png));
    }

    [Fact]
    public void ReadDimensions_ReturnsNull_WhenPngHasNoIhdr()
    {
        var bytes = TestImages.Png(10, 10);
        bytes[12] = (byte)'X';                       // corrupt chunk type
        Assert.Null(ImageFormats.ReadDimensions(bytes, ImageFormats.Png));
    }

    [Theory]
    [InlineData(900, "900 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1024L * 1024 * 3 / 2, "1.5 MB")]
    public void DescribeBytes_IsReadable(long bytes, string expected)
        => Assert.Equal(expected, ImageFormats.DescribeBytes(bytes));

    [Theory]
    [InlineData("a.png", ImageFormats.Png)]
    [InlineData("a.JPG", ImageFormats.Jpeg)]
    [InlineData("a.jpeg", ImageFormats.Jpeg)]
    [InlineData("a.gif", ImageFormats.Gif)]
    [InlineData("a.webp", ImageFormats.Webp)]
    [InlineData("a.txt", null)]
    public void MediaTypeFromExtension_MapsKnownExtensions(string path, string? expected)
        => Assert.Equal(expected, ImageFormats.MediaTypeFromExtension(path));

    // ── DescribeImageFile ──

    [Fact]
    public void DescribeImageFile_NamesTypeDimensionsAndSize()
    {
        using var dir = new TempDir();
        var path = dir.Write("wide.png", [.. TestImages.Png(1920, 1080), .. new byte[61000]]);

        var described = ImageFormats.DescribeImageFile(path);

        Assert.Equal("image/png, 1920×1080, 60 KB", described);
    }

    [Fact]
    public void DescribeImageFile_IsNullForDocumentsAndMissingFiles()
    {
        using var dir = new TempDir();

        Assert.Null(ImageFormats.DescribeImageFile(dir.Write("notes.txt", "just text")));
        Assert.Null(ImageFormats.DescribeImageFile(Path.Combine(dir.Path, "gone.png")));
    }
}
