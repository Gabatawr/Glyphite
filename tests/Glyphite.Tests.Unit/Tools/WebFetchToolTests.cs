using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

/// <summary>
/// fetch_web returns text, so an image URL must be refused rather than decoded into mojibake —
/// the same contract read_file follows. These tests pin that, and pin that documents still work.
/// </summary>
public class WebFetchToolTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Response(byte[] body, string contentType,
        HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } }
        };

    private static async Task<string> Fetch(byte[] body, string contentType, string url = "https://example.com/thing")
    {
        var handler = new StubHandler(_ => Response(body, contentType));
        using var http = new HttpClient(handler);
        return await WebFetchTool.FetchUrl(url, "text", http);
    }

    // ── Images are refused ──

    [Fact]
    public async Task ImageBody_IsRefusedWithAPointerToViewImage()
    {
        var result = await Fetch(TestImages.Png(400, 200), "image/png", "https://example.com/chart.png");

        Assert.Contains("is an image", result);
        Assert.Contains("image/png", result);
        Assert.Contains("400×200", result);
        Assert.Contains("view_image", result);
        Assert.Contains("https://example.com/chart.png", result);
    }

    [Fact]
    public async Task ImageMislabeledAsText_IsStillRefused()
    {
        // Servers lie: sniffing the bytes must win over the declared type.
        var result = await Fetch(TestImages.Gif(64, 32), "text/plain");

        Assert.Contains("is an image", result);
        Assert.Contains("image/gif", result);
        Assert.Contains("view_image", result);
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpeg")]
    [InlineData("image/gif", "gif")]
    [InlineData("image/webp", "webp")]
    public async Task EverySupportedContainer_IsRefused(string contentType, string fixture)
    {
        byte[] body = fixture switch
        {
            "png" => TestImages.Png(10, 10),
            "jpeg" => TestImages.Jpeg(10, 10),
            "gif" => TestImages.Gif(10, 10),
            _ => TestImages.WebpLossless(10, 10),
        };

        var result = await Fetch(body, contentType);

        Assert.Contains("is an image", result);
        Assert.Contains("view_image", result);
    }

    // ── Documents still work ──

    [Fact]
    public async Task HtmlPage_IsStillStrippedToText()
    {
        var html = Encoding.UTF8.GetBytes("<html><body><h1>Hello</h1><p>world</p></body></html>");

        var result = await Fetch(html, "text/html");

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public async Task HtmlPage_BehindAnImageLookingUrl_IsStillText()
    {
        // A .png in the URL proves nothing — only the body decides.
        var html = Encoding.UTF8.GetBytes("<html><body>not a picture</body></html>");

        var result = await Fetch(html, "text/html", "https://example.com/pic.png");

        Assert.Contains("not a picture", result);
        Assert.DoesNotContain("view_image", result);
    }

    [Fact]
    public async Task Svg_StaysADocument()
    {
        // SVG is text, and view_image cannot display it — so it must not be refused.
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>diagram</text></svg>");

        var result = await Fetch(svg, "image/svg+xml");

        Assert.Contains("diagram", result);
        Assert.DoesNotContain("view_image", result);
    }

    [Fact]
    public async Task PlainText_IsReturnedUnchanged()
    {
        var result = await Fetch(Encoding.UTF8.GetBytes("plain text"), "text/plain");

        Assert.Equal("plain text", result);
    }

    // ── Errors ──

    [Fact]
    public async Task HttpFailure_StillReportsTheError()
    {
        var handler = new StubHandler(_ => Response([], "text/html", HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        var result = await WebFetchTool.FetchUrl("https://example.com/gone", "text", http);

        Assert.StartsWith("Error fetching URL", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url")]
    public async Task BadUrl_IsRejectedWithoutARequest(string url)
    {
        var handler = new StubHandler(_ => Response([], "text/html"));
        using var http = new HttpClient(handler);

        var result = await WebFetchTool.FetchUrl(url, "text", http);

        Assert.StartsWith("Error", result);
        Assert.Empty(handler.Requests);
    }

    // ── Markup noise and size ──

    [Fact]
    public async Task ScriptAndStyleBodies_AreDroppedWithTheirTags()
    {
        // Stripping tags alone leaves the JavaScript in the text. On a modern docs site that was
        // 2.6 MB out of a 2.6 MB result — none of it worth a token.
        var html = Encoding.UTF8.GetBytes(
            "<html><head><style>.a{color:red}</style>"
            + "<script>var x = document.querySelector('p');self.__next_f.push([1,\"blob\"])</script>"
            + "</head><body><p>Real words</p></body></html>");

        var result = await Fetch(html, "text/html");

        Assert.Equal("Real words", result);
    }

    [Fact]
    public async Task UnterminatedScript_TakesTheTailWithIt()
    {
        var html = Encoding.UTF8.GetBytes("<html><body><p>kept</p><script>var a = 1; var b = 2;</body></html>");

        var result = await Fetch(html, "text/html");

        Assert.Equal("kept", result);
    }

    [Fact]
    public async Task PageOverTheCharacterLimit_IsCappedAndSaysSo()
    {
        var body = Encoding.UTF8.GetBytes(
            "<html><body>" + string.Join(" ", Enumerable.Repeat("word", 20_000)) + "</body></html>");
        var handler = new StubHandler(_ => Response(body, "text/html"));
        using var http = new HttpClient(handler);

        var result = await WebFetchTool.FetchUrl("https://example.com/huge", "text", http, maxChars: 1000);

        Assert.Contains("[Page truncated at 1000 chars. Full text length:", result);
        Assert.True(result.Length < 1300, $"got {result.Length} chars");
    }

    [Fact]
    public async Task DefaultLimit_KeepsOnePageFromEatingTheContext()
    {
        var body = Encoding.UTF8.GetBytes("<html><body>" + new string('a', 400_000) + "</body></html>");
        var handler = new StubHandler(_ => Response(body, "text/html"));
        using var http = new HttpClient(handler);

        var result = await WebFetchTool.FetchUrl("https://example.com/enormous", "text", http);

        Assert.Contains("[Page truncated at 100000 chars", result);
        Assert.True(result.Length < 101_000, $"got {result.Length} chars");
    }

    [Fact]
    public async Task Download_StopsAtTheByteCeiling()
    {
        var body = Encoding.UTF8.GetBytes("<html><body>" + new string('a', 100_000) + "</body></html>");
        var handler = new StubHandler(_ => Response(body, "text/html"));
        using var http = new HttpClient(handler);

        var result = await WebFetchTool.FetchUrl("https://example.com/big", "text", http, maxChars: 0, maxBytes: 5_000);

        Assert.True(result.Length <= 5_000, $"got {result.Length} chars");
    }

    // ── Detection itself ──

    [Fact]
    public void FindImageMediaType_SniffedBytesWinOverTheDeclaredType()
    {
        Assert.Equal("image/png", WebFetchTool.FindImageMediaType(TestImages.Png(4, 4), "text/html"));
    }

    [Fact]
    public void FindImageMediaType_LeavesDocumentsAlone()
    {
        Assert.Null(WebFetchTool.FindImageMediaType(Encoding.UTF8.GetBytes("<html>hi</html>"), "text/html"));
        Assert.Null(WebFetchTool.FindImageMediaType(Encoding.UTF8.GetBytes("<svg/>"), "image/svg+xml"));
        Assert.Null(WebFetchTool.FindImageMediaType([], null));
    }
}
