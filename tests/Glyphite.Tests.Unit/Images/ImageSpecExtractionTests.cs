using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Tests.Unit.Support;
using Xunit;

namespace Glyphite.Tests.Unit.Images;

/// <summary>
/// Scanning a user message for image references. The scanner must be conservative: a false
/// positive silently attaches a picture the model did not need, so only certain images qualify.
/// </summary>
public class ImageSpecExtractionTests
{
    private static ImageOptions Opts(Action<ImageOptions>? tweak = null)
    {
        var opts = new ImageOptions();
        tweak?.Invoke(opts);
        return opts;
    }

    [Fact]
    public void ExistingLocalImage_IsExtracted()
    {
        using var dir = new TempDir();
        var path = dir.Write("chart.png", TestImages.Png(10, 10));

        var specs = ImageLoader.ExtractSpecs($"look at {path} and tell me", Opts(), dir.Path);

        Assert.Equal(path, Assert.Single(specs));
    }

    [Fact]
    public void RelativePath_ResolvesAgainstDefaultDirectory()
    {
        using var dir = new TempDir();
        dir.Write("diagram.webp", TestImages.WebpLossless(4, 4));

        Assert.Equal(["diagram.webp"], ImageLoader.ExtractSpecs("what is this diagram.webp", Opts(), dir.Path));
    }

    [Fact]
    public void MissingFile_IsNotExtracted()
    {
        using var dir = new TempDir();

        Assert.Empty(ImageLoader.ExtractSpecs("look at ghost.png", Opts(), dir.Path));
    }

    [Fact]
    public void ImageExtensionButNotAnImage_IsNotExtracted()
    {
        using var dir = new TempDir();
        dir.Write("notes.png", "just text");

        Assert.Empty(ImageLoader.ExtractSpecs("open notes.png", Opts(), dir.Path));
    }

    [Fact]
    public void RealImageWithoutImageExtension_IsNotExtracted()
    {
        // Scanning is extension-gated on purpose — otherwise every path in a message would be opened.
        using var dir = new TempDir();
        dir.Write("payload.bin", TestImages.Png1x1);

        Assert.Empty(ImageLoader.ExtractSpecs("check payload.bin", Opts(), dir.Path));
    }

    [Fact]
    public void QuotedPathWithSpaces_IsExtracted()
    {
        using var dir = new TempDir();
        var path = dir.Write("my shot.png", TestImages.Png1x1);

        Assert.Equal([path], ImageLoader.ExtractSpecs($"look at \"{path}\" carefully", Opts(), dir.Path));
        Assert.Equal([path], ImageLoader.ExtractSpecs($"look at `{path}` carefully", Opts(), dir.Path));
    }

    [Theory]
    [InlineData("here is {0}.")]
    [InlineData("here is ({0})")]
    [InlineData("here is <{0}>,")]
    [InlineData("here is \"{0}\"")]
    public void WrappingPunctuation_IsTrimmed(string template)
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png1x1);

        Assert.Equal([path], ImageLoader.ExtractSpecs(string.Format(template, path), Opts(), dir.Path));
    }

    [Fact]
    public void UrlWithImageExtension_IsExtracted()
    {
        var specs = ImageLoader.ExtractSpecs("compare with https://cdn.example.com/a.png here", Opts());

        Assert.Equal(["https://cdn.example.com/a.png"], specs);
    }

    [Fact]
    public void UrlWithQueryString_IsExtracted()
    {
        var specs = ImageLoader.ExtractSpecs("https://cdn.example.com/a.jpg?w=800", Opts());

        Assert.Equal(["https://cdn.example.com/a.jpg?w=800"], specs);
    }

    [Fact]
    public void UrlWithoutImageExtension_IsSkippedByDefault()
    {
        Assert.Empty(ImageLoader.ExtractSpecs("read https://example.com/article", Opts()));
    }

    [Fact]
    public void UrlMatchingAlways_TakesAnyUrl()
    {
        var specs = ImageLoader.ExtractSpecs(
            "check https://photos.example.com/photo-123", Opts(o => o.UrlMatching = "always"));

        Assert.Equal(["https://photos.example.com/photo-123"], specs);
    }

    [Fact]
    public void UrlMatchingNever_TakesNothing()
    {
        Assert.Empty(ImageLoader.ExtractSpecs(
            "https://cdn.example.com/a.png", Opts(o => o.UrlMatching = "never")));
    }

    [Fact]
    public void LocalhostUrl_IsExtracted()
    {
        // Reachable only from this machine, but still an image the user clearly means.
        var specs = ImageLoader.ExtractSpecs("what is on http://localhost:3000/shot.png ?", Opts());

        Assert.Equal(["http://localhost:3000/shot.png"], specs);
    }

    [Fact]
    public void FileUrl_IsExtracted()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.png", TestImages.Png1x1);

        Assert.Equal([$"file://{path}"], ImageLoader.ExtractSpecs($"file://{path}", Opts()));
    }

    [Fact]
    public void DataUrl_IsExtracted()
    {
        var spec = "data:image/png;base64," + Convert.ToBase64String(TestImages.Png1x1);

        Assert.Equal([spec], ImageLoader.ExtractSpecs($"here is {spec}", Opts()));
    }

    [Fact]
    public void SeveralReferences_KeepTheirOrder()
    {
        using var dir = new TempDir();
        var first = dir.Write("a.png", TestImages.Png1x1);
        var second = dir.Write("b.jpg", TestImages.Jpeg(4, 4));

        Assert.Equal([first, second],
            ImageLoader.ExtractSpecs($"first {first} then {second}", Opts(), dir.Path));
    }

    [Fact]
    public void RepeatedReference_IsExtractedOnce()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.png", TestImages.Png1x1);

        Assert.Single(ImageLoader.ExtractSpecs($"{path} and again {path}", Opts(), dir.Path));
    }

    [Fact]
    public void MaxImagesPerRequest_IsRespected()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 5; i++)
            dir.Write($"i{i}.png", TestImages.Png1x1);

        var specs = ImageLoader.ExtractSpecs(
            "i0.png i1.png i2.png i3.png i4.png", Opts(o => o.MaxImagesPerRequest = 2), dir.Path);

        Assert.Equal(2, specs.Count);
    }

    [Fact]
    public void PlainText_YieldsNothing()
    {
        Assert.Empty(ImageLoader.ExtractSpecs("why does the png processing fail?", Opts()));
        Assert.Empty(ImageLoader.ExtractSpecs("", Opts()));
        Assert.Empty(ImageLoader.ExtractSpecs(null, Opts()));
    }

    [Fact]
    public void AutoAttachDisabled_YieldsNothing()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.png", TestImages.Png1x1);

        Assert.Empty(ImageLoader.ExtractSpecs($"look at {path}", Opts(o => o.AutoAttach = false), dir.Path));
    }

    [Fact]
    public void ImageSupportDisabled_YieldsNothing()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.png", TestImages.Png1x1);

        Assert.Empty(ImageLoader.ExtractSpecs($"look at {path}", Opts(o => o.Enabled = false), dir.Path));
    }

    [Fact]
    public void CustomExtensions_AreHonoured()
    {
        using var dir = new TempDir();
        dir.Write("shot.bmp", TestImages.Png1x1);

        Assert.Single(ImageLoader.ExtractSpecs("shot.bmp", Opts(o => o.Extensions = [".bmp"]), dir.Path));
    }
}
