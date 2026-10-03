using System.Net;
using System.Text;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glyphite.Tests.Unit.Images;

public class ImageLoaderTests
{
    private static ImageOptions Opts(Action<ImageOptions>? tweak = null)
    {
        var opts = new ImageOptions();
        tweak?.Invoke(opts);
        return opts;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Bytes(byte[] body, string contentType = "image/png")
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } } };

    private static ImageLoader Loader(StubHandler handler)
        => new(new HttpClient(handler));

    // ── Local files ──────────────────────────────────────────────────

    [Fact]
    public async Task LocalFile_IsInlined_WithDetectedTypeAndDimensions()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png1x1);

        var (payload, error) = await new ImageLoader().LoadAsync(path, Opts());

        Assert.Null(error);
        Assert.NotNull(payload);
        Assert.True(payload!.Inline);
        Assert.Equal(ImageFormats.Png, payload.MediaType);
        Assert.Equal(TestImages.Png1x1.Length, payload.Bytes);
        Assert.Equal(1, payload.Width);
        Assert.Equal(1, payload.Height);

        var content = Assert.IsType<DataContent>(payload.Content);
        Assert.Equal(ImageFormats.Png, content.MediaType);
        Assert.Equal(TestImages.Png1x1, content.Data.ToArray());
    }

    [Fact]
    public async Task LocalFile_RelativePathResolvesAgainstDefaultDirectory()
    {
        using var dir = new TempDir();
        dir.Write("nested.png", TestImages.Png(8, 4));

        var (payload, error) = await new ImageLoader().LoadAsync("nested.png", Opts(), dir.Path);

        Assert.Null(error);
        Assert.Equal(8, payload!.Width);
        Assert.Equal(4, payload.Height);
    }

    [Fact]
    public async Task LocalFile_ContentWinsOverExtension()
    {
        // .txt holding a PNG: rejected by extension scanning, but explicit calls trust the bytes.
        using var dir = new TempDir();
        var path = dir.Write("actually.png", TestImages.Png1x1);
        File.Move(path, Path.Combine(dir.Path, "actually.dat"));

        var (payload, error) = await new ImageLoader().LoadAsync(
            Path.Combine(dir.Path, "actually.dat"), Opts());

        Assert.Null(error);
        Assert.Equal(ImageFormats.Png, payload!.MediaType);
    }

    [Fact]
    public async Task LocalFile_Missing_ReturnsActionableError()
    {
        var (payload, error) = await new ImageLoader().LoadAsync("/nope/missing.png", Opts());

        Assert.Null(payload);
        Assert.Contains("File not found", error);
    }

    [Fact]
    public async Task LocalFile_NotAnImage_IsRejected()
    {
        using var dir = new TempDir();
        var path = dir.Write("notes.png", "just text, not a picture");

        var (payload, error) = await new ImageLoader().LoadAsync(path, Opts());

        Assert.Null(payload);
        Assert.Contains("Not a supported image", error);
    }

    [Fact]
    public async Task LocalFile_Empty_IsRejected()
    {
        using var dir = new TempDir();
        var path = dir.Write("empty.png", []);

        var (payload, error) = await new ImageLoader().LoadAsync(path, Opts());

        Assert.Null(payload);
        Assert.Contains("empty", error);
    }

    [Fact]
    public async Task LocalFile_OverSizeLimit_IsRejectedBeforeReading()
    {
        using var dir = new TempDir();
        var path = dir.Write("big.png", TestImages.Png(64, 64));

        var (payload, error) = await new ImageLoader().LoadAsync(path, Opts(o => o.MaxImageBytes = 10));

        Assert.Null(payload);
        Assert.Contains("limit", error);
        Assert.Contains("big.png", error);
    }

    [Fact]
    public async Task LocalFile_OverDimensionLimit_IsRejected()
    {
        using var dir = new TempDir();
        var path = dir.Write("huge.png", TestImages.Png(9000, 10));

        var (payload, error) = await new ImageLoader().LoadAsync(path, Opts());

        Assert.Null(payload);
        Assert.Contains("9000×10", error);
        Assert.Contains("8192px", error);
    }

    // ── Data URLs ────────────────────────────────────────────────────

    [Fact]
    public async Task DataUrl_Base64_IsDecoded()
    {
        var spec = "data:image/png;base64," + Convert.ToBase64String(TestImages.Png1x1);

        var (payload, error) = await new ImageLoader().LoadAsync(spec, Opts());

        Assert.Null(error);
        Assert.True(payload!.Inline);
        Assert.Equal(ImageFormats.Png, payload.MediaType);
        Assert.Equal(TestImages.Png1x1.Length, payload.Bytes);
    }

    [Fact]
    public async Task DataUrl_Malformed_IsRejected()
    {
        var (payload, error) = await new ImageLoader().LoadAsync("data:image/png;base64", Opts());
        Assert.Null(payload);
        Assert.Contains("Malformed", error);

        var (payload2, error2) = await new ImageLoader().LoadAsync("data:image/png;base64,!!!not-base64!!!", Opts());
        Assert.Null(payload2);
        Assert.Contains("base64", error2);
    }

    [Fact]
    public async Task DataUrl_NonBase64_PreservesRawOctets()
    {
        // Percent-escapes are octets: the PNG signature (0x89 …) and every byte ≥ 0x80 must survive
        // verbatim. Decoding through a UTF-8 string used to mangle them and break the sniff.
        var encoded = string.Concat(TestImages.Png1x1.Select(b => $"%{b:X2}"));

        var (payload, error) = await new ImageLoader().LoadAsync("data:image/png," + encoded, Opts());

        Assert.Null(error);
        Assert.True(payload!.Inline);
        Assert.Equal(TestImages.Png1x1, Assert.IsType<DataContent>(payload.Content).Data.ToArray());
    }

    // ── URLs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicUrl_WithQueryString_StillGetsItsType()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, _) = await Loader(handler).LoadAsync(
            "https://cdn.example.com/a.jpg?w=800&h=600", Opts());

        Assert.Equal(ImageFormats.Jpeg, payload!.MediaType);
    }

    [Theory]
    [InlineData("http://localhost:3000/shot.png")]
    [InlineData("http://127.0.0.1/shot.png")]
    [InlineData("http://192.168.1.10/shot.png")]
    [InlineData("http://10.0.0.5/shot.png")]
    [InlineData("http://172.16.4.4/shot.png")]
    [InlineData("http://devbox/shot.png")]
    public async Task UnreachableUrl_IsDownloadedAndInlined(string url)
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, error) = await Loader(handler).LoadAsync(url, Opts());

        Assert.Null(error);
        Assert.True(payload!.Inline);                 // the provider cannot reach these hosts
        Assert.Equal(url, Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task UrlModeInline_AlwaysDownloads()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, _) = await Loader(handler).LoadAsync(
            "https://example.com/pic.png", Opts(o => o.UrlMode = "inline"));

        Assert.True(payload!.Inline);
        Assert.Single(handler.Urls);
    }

    [Theory]
    [InlineData("Inline")]
    [InlineData("INLINE")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public async Task UrlMode_IsCaseInsensitive(string mode)
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, _) = await Loader(handler).LoadAsync(
            "https://example.com/pic.png", Opts(o => o.UrlMode = mode));

        // "inline" always inlines; "auto" (adaptive) passes a public URL through.
        Assert.Equal(mode.Equals("inline", StringComparison.OrdinalIgnoreCase), payload!.Inline);
    }

    [Fact]
    public async Task UrlModeAuto_IsAdaptive_PublicPassesThrough_UnreachableHostIsInlined()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));

        // A public URL is handed to the provider as UriContent — the provider downloads it, so no
        // bytes travel through the agent.
        var (pub, error) = await Loader(handler).LoadAsync("https://example.com/pic.png", Opts());
        Assert.Null(error);
        Assert.False(pub!.Inline);
        Assert.Equal(0, pub.Bytes);
        Assert.Empty(handler.Urls);
        var content = Assert.IsType<UriContent>(pub.Content);
        Assert.Equal("https://example.com/pic.png", content.Uri.ToString());
        Assert.Equal(ImageFormats.Png, content.MediaType);

        // A host the provider can never reach is fetched here instead.
        var (local, _) = await Loader(handler).LoadAsync("http://localhost/shot.png", Opts());
        Assert.True(local!.Inline);
        Assert.Equal("http://localhost/shot.png", Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task OverlongUrl_IsInlined_EvenInAutoMode()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, _) = await Loader(handler).LoadAsync(
            "https://example.com/pic.png?token=" + new string('x', 200),
            Opts(o => o.MaxUrlLength = 64));

        Assert.True(payload!.Inline);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Url_HttpError_IsReported()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var (payload, error) = await Loader(handler).LoadAsync("http://localhost/gone.png", Opts());

        Assert.Null(payload);
        Assert.Contains("404", error);
    }

    [Fact]
    public async Task Url_ServingNonImage_IsRejected()
    {
        var handler = new StubHandler(_ => Bytes(Encoding.UTF8.GetBytes("<html>hi</html>"), "text/html"));
        var (payload, error) = await Loader(handler).LoadAsync("http://localhost/page.png", Opts());

        Assert.Null(payload);
        Assert.Contains("Not a supported image", error);
    }

    [Fact]
    public async Task Url_LargerThanLimit_IsRejectedMidStream()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png(64, 64)));
        var (payload, error) = await Loader(handler).LoadAsync(
            "http://localhost/big.png", Opts(o => o.MaxImageBytes = 10));

        Assert.Null(payload);
        Assert.Contains("limit", error);
    }

    // ── Detail level ─────────────────────────────────────────────────

    [Fact]
    public async Task Detail_IsAppliedFromOptions_AndOmittedForAuto()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.png", TestImages.Png1x1);

        var (low, _) = await new ImageLoader().LoadAsync(path, Opts(o => o.Detail = "low"));
        Assert.Equal("low", low!.Content.AdditionalProperties!["detail"]);

        var (auto, _) = await new ImageLoader().LoadAsync(path, Opts());
        Assert.True(auto!.Content.AdditionalProperties is null
            || !auto.Content.AdditionalProperties.ContainsKey("detail"));
    }

    [Fact]
    public async Task Detail_PerCallOverridesOptions()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.png", TestImages.Png1x1);

        var (payload, _) = await new ImageLoader().LoadAsync(path, Opts(o => o.Detail = "low"), detail: "original");

        Assert.Equal("original", payload!.Content.AdditionalProperties!["detail"]);
    }

    // ── Describe ─────────────────────────────────────────────────────

    [Fact]
    public async Task Describe_ListsPathTypeDimensionsAndSize()
    {
        using var dir = new TempDir();
        // Padded past 1 KB so the size reads in KB; the header stays intact.
        var path = dir.Write("shot.png", [.. TestImages.Png(1920, 1080), .. new byte[2000]]);

        var (payload, _) = await new ImageLoader().LoadAsync(path, Opts());
        var described = payload!.Describe();

        Assert.Contains("shot.png", described);
        Assert.Contains("image/png", described);
        Assert.Contains("1920×1080", described);
        Assert.Contains("KB", described);
    }

    [Fact]
    public async Task Describe_MarksPassedThroughUrls()
    {
        var (payload, _) = await Loader(new StubHandler(_ => Bytes(TestImages.Png1x1)))
            .LoadAsync("https://example.com/pic.png", Opts());

        Assert.Contains("URL", payload!.Describe());
    }

    // ── Host classification ──────────────────────────────────────────

    [Theory]
    [InlineData("example.com", false)]
    [InlineData("cdn.example.com", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("localhost", true)]
    [InlineData("intranet", true)]
    [InlineData("myhost.lan", true)]
    [InlineData("docs.internal", true)]
    [InlineData("172.31.0.1", true)]
    [InlineData("172.32.0.1", false)]
    public void IsNonPublicHost_ClassifiesHosts(string host, bool expected)
        => Assert.Equal(expected, ImageLoader.IsNonPublicHost(host));

    // ── Identity (dedup key) ─────────────────────────────────────────

    [Fact]
    public async Task Key_IsTheResolvedPath_SoOneFileSpelledTwiceDedupes()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png1x1);

        var (absolute, _) = await new ImageLoader().LoadAsync(path, Opts());
        var (relative, _) = await new ImageLoader().LoadAsync("shot.png", Opts(), dir.Path);

        Assert.Equal(absolute!.Key, relative!.Key);
    }

    [Fact]
    public async Task Key_TellsTwoDifferentFilesApart()
    {
        using var dir = new TempDir();
        var first = dir.Write("a.png", TestImages.Png1x1);
        var second = dir.Write("b.png", TestImages.Png1x1);

        var (a, _) = await new ImageLoader().LoadAsync(first, Opts());
        var (b, _) = await new ImageLoader().LoadAsync(second, Opts());

        Assert.NotEqual(a!.Key, b!.Key);
    }

    [Fact]
    public async Task Key_IsTheUrl_ForAHandedOverImage()
    {
        var (payload, _) = await Loader(new StubHandler(_ => Bytes(TestImages.Png1x1)))
            .LoadAsync("https://example.com/pic.png", Opts());

        Assert.False(payload!.Inline);
        Assert.Equal("https://example.com/pic.png", payload.Key);
    }

    [Fact]
    public async Task Key_IsAContentHash_ForDataUrls()
    {
        var spec = "data:image/png;base64," + Convert.ToBase64String(TestImages.Png1x1);

        var (payload, _) = await new ImageLoader().LoadAsync(spec, Opts());
        var (again, _) = await new ImageLoader().LoadAsync(spec, Opts());

        Assert.StartsWith("data:", payload!.Key);
        Assert.Equal(payload.Key, again!.Key);
    }
}