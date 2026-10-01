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

    // ── URLs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UrlModePassthrough_LeavesAPublicUrlToTheProvider()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, error) = await Loader(handler).LoadAsync(
            "https://example.com/pic.png", Opts(o => o.UrlMode = "passthrough"));

        Assert.Null(error);
        Assert.False(payload!.Inline);
        Assert.Equal(0, payload.Bytes);
        Assert.Empty(handler.Urls);                   // the provider fetches it, not us
        var content = Assert.IsType<UriContent>(payload.Content);
        Assert.Equal("https://example.com/pic.png", content.Uri.ToString());
        Assert.Equal(ImageFormats.Png, content.MediaType);
    }

    [Fact]
    public async Task PublicUrl_WithQueryString_StillGetsItsType()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, _) = await Loader(handler).LoadAsync(
            "https://cdn.example.com/a.jpg?w=800&h=600", Opts(o => o.UrlMode = "passthrough"));

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

    [Fact]
    public async Task UrlModePassthrough_StillInlinesHostsTheProviderCannotReach()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, _) = await Loader(handler).LoadAsync(
            "http://localhost/shot.png", Opts(o => o.UrlMode = "passthrough"));

        Assert.True(payload!.Inline);
        Assert.Equal("http://localhost/shot.png", Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task UrlModeAuto_InlinesPublicUrls_SoABadUrlCannotKillTheTurn()
    {
        // Handing the URL over lets the provider fetch it; if that download fails the whole
        // request dies with an opaque 400 and the turn is lost. Auto therefore never delegates.
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, error) = await Loader(handler).LoadAsync("https://example.com/pic.png", Opts());

        Assert.Null(error);
        Assert.True(payload!.Inline);
        Assert.Equal("https://example.com/pic.png", Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task OverlongUrl_IsInlined_EvenInPassthroughMode()
    {
        var handler = new StubHandler(_ => Bytes(TestImages.Png1x1));
        var (payload, _) = await Loader(handler).LoadAsync(
            "https://example.com/pic.png?token=" + new string('x', 200),
            Opts(o => { o.UrlMode = "passthrough"; o.MaxUrlLength = 64; }));

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
            .LoadAsync("https://example.com/pic.png", Opts(o => o.UrlMode = "passthrough"));

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
}
