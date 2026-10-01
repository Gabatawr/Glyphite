using Glyphite.Host.Images;
using Glyphite.Host.Services;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glyphite.Tests.Unit.Images;

/// <summary>
/// How an image a tool loaded actually reaches the model: not through the tool result (images are
/// illegal in tool-role messages) but as a user message injected after each tool batch.
/// </summary>
public class ImagePipelineTests
{
    private static ImagePayload Payload(string spec = "/tmp/a.png")
        => new(new DataContent(TestImages.Png1x1, "image/png"), spec, "image/png",
            TestImages.Png1x1.Length, 1, 1, true);

    private static AIFunction ViewImageTool(ImageAttachmentSink sink, ImagePayload payload, string? note = "what is in it?")
        => AIFunctionFactory.Create(
            (string source) =>
            {
                sink.Add(payload, note);
                return $"loaded {source}";
            },
            "view_image");

    private static ChatResponseUpdate TextUpdate(string text)
        => new(ChatRole.Assistant, [new TextContent(text)]);

    private static ChatResponseUpdate ToolCallUpdate(string callId, string name, params (string K, object? V)[] args)
        => new(ChatRole.Assistant, [new FunctionCallContent(callId, name,
            new Dictionary<string, object?>(args.Select(a => new KeyValuePair<string, object?>(a.K, a.V))))]);

    private static async Task<List<ChatResponseUpdate>> Consume(
        FailSafeChatClient client, ChatOptions options)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "look")], options))
            updates.Add(u);
        return updates;
    }

    [Fact]
    public async Task ToolLoadedImage_ReachesTheModelAsAUserMessage()
    {
        var sink = new ImageAttachmentSink();
        var payload = Payload();
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("c1", "view_image", ("source", "/tmp/a.png"))],
            [TextUpdate("done")]);

        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance, sink);
        await Consume(client, new ChatOptions { Tools = [ViewImageTool(sink, payload)] });

        var second = fake.ReceivedMessages.ElementAt(1).ToList();

        // The tool's own result must stay text-only.
        var toolMessage = second.First(m => m.Role == ChatRole.Tool);
        Assert.Empty(toolMessage.Contents.OfType<DataContent>());

        // Exactly one extra user message, carrying the picture and its captions.
        var imageMessage = Assert.Single(second,
            m => m.Role == ChatRole.User && m.Contents.OfType<DataContent>().Any());
        Assert.Contains(imageMessage.Contents.OfType<TextContent>(), t => t.Text == "what is in it?");
        Assert.Contains(imageMessage.Contents.OfType<TextContent>(), t => t.Text!.StartsWith("↑"));
        Assert.Single(imageMessage.Contents.OfType<DataContent>());

        // The original user message is untouched.
        Assert.Equal(2, second.Count(m => m.Role == ChatRole.User));
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task WithoutPendingImages_NoExtraUserMessageAppears()
    {
        var sink = new ImageAttachmentSink();
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("c1", "read_file", ("path", "/tmp/a.txt"))],
            [TextUpdate("done")]);

        var readTool = AIFunctionFactory.Create((string path) => "text", "read_file");
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance, sink);
        await Consume(client, new ChatOptions { Tools = [readTool] });

        var second = fake.ReceivedMessages.ElementAt(1).ToList();
        Assert.Single(second, m => m.Role == ChatRole.User);
    }

    [Fact]
    public async Task SeveralImages_ArriveTogetherInOneMessage()
    {
        var sink = new ImageAttachmentSink();
        var first = Payload("/tmp/a.png");
        var second = Payload("/tmp/b.png");
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("c1", "view_image", ("source", "/tmp/a.png")),
             ToolCallUpdate("c2", "view_image", ("source", "/tmp/b.png"))],
            [TextUpdate("done")]);

        var tool = AIFunctionFactory.Create(
            (string source) =>
            {
                sink.Add(string.Equals(source, "/tmp/a.png", StringComparison.Ordinal) ? first : second, null);
                return "loaded";
            },
            "view_image");

        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance, sink);
        await Consume(client, new ChatOptions { Tools = [tool] });

        var second_call = fake.ReceivedMessages.ElementAt(1).ToList();
        var imageMessage = Assert.Single(second_call,
            m => m.Role == ChatRole.User && m.Contents.OfType<DataContent>().Any());
        Assert.Equal(2, imageMessage.Contents.OfType<DataContent>().Count());
        Assert.Contains(imageMessage.Contents.OfType<TextContent>(), t => t.Text!.Contains("2 images"));
    }

    [Fact]
    public async Task NonStreamingPath_AlsoInjectsImages()
    {
        var sink = new ImageAttachmentSink();
        var payload = Payload();
        var fake = new FakeChatClient();
        fake.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c1", "view_image", new Dictionary<string, object?> { ["source"] = "/tmp/a.png" })])]));
        fake.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));

        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance, sink);
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "look")],
            new ChatOptions { Tools = [ViewImageTool(sink, payload)] },
            default);

        var second = fake.ReceivedMessages.ElementAt(1).ToList();
        Assert.Contains(second, m => m.Role == ChatRole.User && m.Contents.OfType<DataContent>().Any());
    }

    [Fact]
    public void Sink_DrainsOnce_SoAnImageIsNeverSentTwice()
    {
        var sink = new ImageAttachmentSink();
        sink.Add(Payload());

        Assert.Equal(1, sink.Count);
        Assert.Single(sink.Drain());
        Assert.Equal(0, sink.Count);
        Assert.Empty(sink.Drain());
    }

    [Fact]
    public void Sink_Clear_DropsWhateverAToolQueued()
    {
        var sink = new ImageAttachmentSink();
        sink.Add(Payload());

        sink.Clear();

        Assert.Equal(0, sink.Count);
    }
}
