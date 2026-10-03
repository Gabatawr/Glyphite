using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Images;

public class ImageToolTests
{
    private static IConfigService Cfg(ImageOptions? opts = null)
    {
        var cfg = Substitute.For<IConfigService>();
        cfg.GetOptionsAsync<ImageOptions>(ImageOptions.Section, Arg.Any<string?>())
            .Returns(opts ?? new ImageOptions());
        return cfg;
    }

    private static (AIFunction Tool, ImageAttachmentSink Sink) Build(ImageOptions? opts = null)
    {
        var sink = new ImageAttachmentSink();
        var tool = ImageTool.AsViewImageFunction(new ImageLoader(), sink, Cfg(opts));
        return (tool, sink);
    }

    /// <summary>
    /// Invoke the tool and unwrap the answer. MEAI hands back a JsonElement for a string result,
    /// so the tests normalise instead of each one casting.
    /// </summary>
    private static async Task<string> Invoke(AIFunction tool, params (string K, object? V)[] args)
    {
        var result = await tool.InvokeAsync(new AIFunctionArguments(args.ToDictionary(a => a.K, a => a.V)));
        return result switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => result?.ToString() ?? ""
        };
    }

    [Fact]
    public void Tool_IsNamed_view_image_And_TakesAPathOrUrl()
    {
        var (tool, _) = Build();

        Assert.Equal("view_image", tool.Name);
        using var schema = JsonDocument.Parse(tool.JsonSchema.GetRawText());
        var properties = schema.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("source", out _));
        Assert.True(properties.TryGetProperty("detail", out _));
        Assert.True(properties.TryGetProperty("question", out _));
        Assert.False(properties.TryGetProperty("ct", out _));   // cancellation is injected, not advertised
    }

    [Fact]
    public async Task LoadingAnImage_QueuesItForTheModel_AndAnswersWithAReceipt()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png(1920, 1080));
        var (tool, sink) = Build();

        var result = await Invoke(tool, ("source", path));

        Assert.Contains("attached", result);
        Assert.Contains("shot.png", result);
        Assert.Contains("1920×1080", result);

        var pending = Assert.Single(sink.Drain());
        Assert.Equal(ImageFormats.Png, pending.Payload.MediaType);
        Assert.Null(pending.Note);
    }

    [Fact]
    public async Task Question_IsCarriedAsTheNoteForTheInjectedMessage()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png1x1);
        var (tool, sink) = Build();

        await Invoke(tool, ("source", path), ("question", "How many buttons are on the panel?"));

        Assert.Equal("How many buttons are on the panel?", Assert.Single(sink.Drain()).Note);
    }

    [Fact]
    public async Task DetailOverride_ReachesTheProviderPart()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png1x1);
        var (tool, sink) = Build();

        await Invoke(tool, ("source", path), ("detail", "low"));

        Assert.Equal("low", Assert.Single(sink.Drain()).Payload.Content.AdditionalProperties!["detail"]);
    }

    [Fact]
    public async Task BrokenReference_ReturnsReadableError_AndQueuesNothing()
    {
        var (tool, sink) = Build();

        var result = await Invoke(tool, ("source", "/nowhere/missing.png"));

        Assert.StartsWith("Error: ", result);
        Assert.Contains("File not found", result);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task DisabledImageSupport_RefusesWithoutLoading()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png1x1);
        var (tool, sink) = Build(new ImageOptions { Enabled = false });

        var result = await Invoke(tool, ("source", path));

        Assert.Contains("disabled", result);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task PassedThroughUrl_IsAnnouncedAsAUrl()
    {
        var (tool, sink) = Build();                  // auto: a public URL is handed to the provider

        var result = await Invoke(tool, ("source", "https://example.com/pic.png"));

        Assert.Contains("image URL is attached", result);
        var pending = Assert.Single(sink.Drain());
        Assert.False(pending.Payload.Inline);
        Assert.IsType<UriContent>(pending.Payload.Content);
    }

    [Fact]
    public async Task BudgetExhausted_ReturnsError_AndQueuesNothingMore()
    {
        using var dir = new TempDir();
        var first = dir.Write("a.png", TestImages.Png1x1);
        var second = dir.Write("b.png", TestImages.Png1x1);
        var (tool, sink) = Build(new ImageOptions { MaxImagesPerRequest = 1 });

        Assert.Contains("attached", await Invoke(tool, ("source", first)));

        var refused = await Invoke(tool, ("source", second));

        Assert.StartsWith("Error: ", refused);
        Assert.Contains("MaxImagesPerRequest", refused);
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public async Task PictureAlreadyInTheConversation_IsNotAttachedASecondTime()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png1x1);
        var (tool, sink) = Build();

        // The turn already put this picture into the opening user message.
        var (attached, error) = await new ImageLoader().LoadAsync(path, new ImageOptions());
        Assert.Null(error);
        sink.Seed([attached!]);

        var result = await Invoke(tool, ("source", path));

        Assert.Contains("Already attached", result);
        Assert.Equal(0, sink.Count);
    }
}