using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Glyphite.Host.Services;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace Glyphite.Tests.Unit.Images;

/// <summary>
/// Captures the exact JSON that reaches the provider when an image is attached through MEAI.
/// The MEAI → OpenAI-SDK mapping of <see cref="DataContent"/> / <see cref="UriContent"/> is not
/// documented, and image support rests entirely on it, so it is pinned here against a fake transport.
/// </summary>
public class ImageWireFormatTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? Url { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"1","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task<(string Body, Uri? Url)> CaptureAsync(bool viaAgentChatClient, params AIContent[] contents)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var openAi = new OpenAIClient(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://api.deepseek.com/v1"),
                Transport = new HttpClientPipelineTransport(http)
            });
        IChatClient chat = openAi.GetChatClient("deepseek-flash").AsIChatClient();
        if (viaAgentChatClient)
            chat = new AgentChatClient(chat, "Agent.Under.Test", "deepseek-flash");

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, contents)]);
        return (handler.Body!, handler.Url);
    }

    [Fact]
    public async Task DataContent_IsSentAsBase64DataUrl_InImageUrlPart()
    {
        var (body, url) = await CaptureAsync(false,
            new TextContent("What is in this image?"),
            new DataContent(TestImages.Png1x1, "image/png"));

        Assert.Equal("https://api.deepseek.com/v1/chat/completions", url!.ToString());

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(System.Text.Json.JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("What is in this image?", content[0].GetProperty("text").GetString());

        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        var imageUrl = content[1].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", imageUrl);
        Assert.Contains(Convert.ToBase64String(TestImages.Png1x1), imageUrl);
    }

    [Fact]
    public async Task UriContent_IsSentAsPlainUrl_InImageUrlPart()
    {
        var (body, _) = await CaptureAsync(false,
            new TextContent("Describe this."),
            new UriContent(new Uri("https://example.com/image.jpg"), "image/jpeg"));

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var part = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[1];

        Assert.Equal("image_url", part.GetProperty("type").GetString());
        Assert.Equal("https://example.com/image.jpg",
            part.GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task ImageSurvives_AgentChatClientWrapper()
    {
        // The real pipeline wraps the client in AgentChatClient (user_id JsonPatch + RawRepresentationFactory).
        // That wrapper must not drop image parts.
        var (body, _) = await CaptureAsync(true,
            new TextContent("look"),
            new DataContent(TestImages.Png1x1, "image/png"));

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Contains("data:image/png;base64,", body);
        Assert.Equal("image_url",
            doc.RootElement.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("Agent.Under.Test", doc.RootElement.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task DetailAdditionalProperty_BecomesImageUrlDetail()
    {
        // MEAI maps AIContent.AdditionalProperties["detail"] into image_url.detail — exactly the
        // shape DeepSeek documents for its detail levels (low / high / original / auto).
        var image = new UriContent(new Uri("https://example.com/image.jpg"), "image/jpeg");
        image.AdditionalProperties = new AdditionalPropertiesDictionary { ["detail"] = "low" };

        var (body, _) = await CaptureAsync(false, new TextContent("x"), image);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var imageUrl = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[1]
            .GetProperty("image_url");

        Assert.Equal("https://example.com/image.jpg", imageUrl.GetProperty("url").GetString());
        Assert.Equal("low", imageUrl.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task DetailAdditionalProperty_AlsoAppliesToInlineImages()
    {
        var image = new DataContent(TestImages.Png1x1, "image/png");
        image.AdditionalProperties = new AdditionalPropertiesDictionary { ["detail"] = "original" };

        var (body, _) = await CaptureAsync(false, new TextContent("x"), image);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var imageUrl = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[1]
            .GetProperty("image_url");

        Assert.StartsWith("data:image/png;base64,", imageUrl.GetProperty("url").GetString());
        Assert.Equal("original", imageUrl.GetProperty("detail").GetString());
    }
}
