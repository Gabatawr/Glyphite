using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Host.Services;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class TurnProcessorTests : IDisposable
{
    private const string AgentId = "test-agent";

    private readonly TestDb _db = new();
    private readonly FakeChatClient _chat = new();
    private readonly IConfigService _cfg = Substitute.For<IConfigService>();
    private readonly IBlockMemoryProvider _memory = Substitute.For<IBlockMemoryProvider>();
    private readonly IToolRegistry _tools = Substitute.For<IToolRegistry>();
    private readonly ISessionConfigLoader _configLoader = Substitute.For<ISessionConfigLoader>();
    private readonly IInstructionProvider _instructions = Substitute.For<IInstructionProvider>();
    private readonly CompactionService _compaction;

    public TurnProcessorTests()
    {
        _compaction = new CompactionService(_db.Blocks, _db.Sessions, _cfg, _chat, NullLogger<CompactionService>.Instance);

        _memory.CurrentExecutedIds.Returns(new AsyncLocal<HashSet<string>?>());
        _memory.BuildContextAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(Task.FromResult(new List<ChatMessage>()));
        _tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>([]));
        _configLoader.LoadConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        _instructions.BuildInstructionsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult("test instructions"));
    }

    public void Dispose() => _db.Dispose();

    private TurnProcessor CreateProcessor() => new(
        _db.Sessions, _db.Blocks, _memory, _chat, _tools, _cfg,
        _compaction, _configLoader, _instructions,
        new ImageLoader(), new ImageAttachmentSink(),
        NullLogger<TurnProcessor>.Instance);

    private void SetupOptions(
        LlmOptions? llm = null,
        AgentOptions? agent = null,
        CompressionOptions? compression = null)
    {
        llm ??= new LlmOptions
        {
            Endpoint = "http://localhost",
            ApiKey = "test-key",
            Model = "test-model",
            ContextWindow = 100_000,
            Models = [new LlmModel { Name = "test-model" }],
        };
        agent ??= new AgentOptions { MaxToolIterations = 5 };
        compression ??= new CompressionOptions { AutoCompress = false, AutoCompressReasoning = false };
        _cfg.GetOptionsAsync<LlmOptions>(LlmOptions.Section, Arg.Any<string?>()).Returns(llm);
        _cfg.GetOptionsAsync<AgentOptions>(AgentOptions.Section, Arg.Any<string?>()).Returns(agent);
        _cfg.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, Arg.Any<string?>()).Returns(compression);
        _cfg.GetOptionsAsync<ImageOptions>(ImageOptions.Section, Arg.Any<string?>()).Returns(new ImageOptions());
    }

    private async Task<List<TurnEvent>> RunTurnAsync(string input = "hello", CancellationToken ct = default)
    {
        var events = new List<TurnEvent>();
        await foreach (var e in CreateProcessor().ProcessAsync(AgentId, input, new ChatOptions(), ct))
            events.Add(e);
        return events;
    }
// ── Turn lifecycle ──

    [Fact]
    public async Task LastIterationUsage_PopulatedAfterTurn()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi")])]);

        var processor = CreateProcessor();
        await foreach (var _ in processor.ProcessAsync(AgentId, "hello", new ChatOptions(), default))
        {
        }

        // OnIterationRecorded fires per iteration → the immutable fallback snapshot is set
        Assert.NotNull(processor.LastIterationUsage);
    }
// ── Validation ──

    [Fact]
    public async Task MissingApiKey_YieldsError_NoChatCall()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions(llm: new LlmOptions
        {
            Endpoint = "http://localhost",
            ApiKey = "",
            Model = "m",
            ContextWindow = 1000,
            Models = [new LlmModel { Name = "m" }],
        });

        var events = await RunTurnAsync();

        var error = Assert.Single(events.OfType<TurnErrorEvent>());
        Assert.Contains("API key", error.Message);
        Assert.Equal(0, _chat.StreamCallCount);
        Assert.Equal(0, _chat.ResponseCallCount);
    }
// ── Happy path ──

    [Fact]
    public async Task HappyPath_AppendsBlocks_EmitsUsageAndComplete()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi there")])]);

        var events = await RunTurnAsync();

        Assert.Contains(events, e => e is TextChunkEvent { Chunk: "hi there" });
        Assert.Contains(events, e => e is UsageTurnEvent);
        Assert.Contains(events, e => e is TurnCompleteEvent);
        Assert.DoesNotContain(events, e => e is TurnErrorEvent);
        Assert.Equal(1, _chat.StreamCallCount);

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        Assert.Contains(blocks, b => b.Type == BlockType.user_message && b.Content == "hello");
        Assert.Contains(blocks, b => b.Type == BlockType.agent_message && b.Content == "hi there");
        Assert.Contains(blocks, b => b.Type == BlockType.turn);
    }
// ── Images ──

    [Fact]
    public async Task ImagePathInMessage_IsAttachedToTheUserMessage_AndMarkedInTheBlock()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("вижу")])]);

        using var dir = new TempDir();
        var imagePath = dir.Write("shot.png", TestImages.Png(320, 200));

        var events = await RunTurnAsync($"что на {imagePath}?");

        // The UI is told, the model gets a real image part, and the stored block gets a cheap marker.
        var notice = Assert.Single(events.OfType<ImageAttachedTurnEvent>());
        Assert.Contains("shot.png", notice.Description);
        Assert.Contains("320×200", notice.Description);

        var sent = _chat.ReceivedMessages[0];
        var userMessage = Assert.Single(sent, m => m.Role == ChatRole.User);
        Assert.Contains(userMessage.Contents.OfType<DataContent>(), c => c.MediaType == "image/png");

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var userBlock = Assert.Single(blocks, b => b.Type == BlockType.user_message);
        Assert.Contains("[image attached:", userBlock.Content);
        Assert.Contains("shot.png", userBlock.Content);
    }

    [Fact]
    public async Task BrokenImagePath_LeavesTheTurnIntact()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);

        var events = await RunTurnAsync("посмотри /nowhere/gone.png");

        Assert.DoesNotContain(events, e => e is ImageAttachedTurnEvent);
        Assert.Contains(events, e => e is TurnCompleteEvent);
        Assert.Equal(1, _chat.StreamCallCount);

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var userBlock = Assert.Single(blocks, b => b.Type == BlockType.user_message);
        Assert.DoesNotContain("[image attached:", userBlock.Content);
    }

    [Fact]
    public async Task AutoAttachDisabled_NoImageIsSent()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _cfg.GetOptionsAsync<ImageOptions>(ImageOptions.Section, Arg.Any<string?>())
            .Returns(new ImageOptions { AutoAttach = false });
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);

        using var dir = new TempDir();
        var imagePath = dir.Write("shot.png", TestImages.Png1x1);

        var events = await RunTurnAsync($"что на {imagePath}?");

        Assert.DoesNotContain(events, e => e is ImageAttachedTurnEvent);
        var sent = _chat.ReceivedMessages[0];
        Assert.DoesNotContain(sent.SelectMany(m => m.Contents).OfType<DataContent>(), _ => true);
    }

// ── Compaction ──

    [Fact]
    public async Task CompactionTrigger_YieldsAutoToolEvent_AppendsCompactedHistory()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions(
            llm: new LlmOptions
            {
                Endpoint = "http://localhost",
                ApiKey = "test-key",
                Model = "test-model",
                ContextWindow = 10_000,
                Models = [new LlmModel { Name = "test-model" }],
            },
            compression: new CompressionOptions { AutoCompress = true, AutoThreshold = 50, AutoCompressReasoning = false });
        await TestData.AppendHistoryAsync(_db.Blocks, AgentId, turns: 3);
        await _db.Sessions.RecordUsageAsync(AgentId, 0, 0, 0, lastRequestHit: 0, lastRequestMiss: 6_000);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "SUMMARY")]));
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi")])]);

        var events = await RunTurnAsync();

        var autoTool = Assert.Single(events.OfType<AutoToolTurnEvent>(), e => e.Name == "compression");
        Assert.Contains("AutoCompress", autoTool.Args);
        Assert.Contains(events, e => e is TextChunkEvent { Chunk: "hi" });

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        Assert.Contains(blocks, b => b.Type == BlockType.auto_tool && b.ToolName == "compression");
        Assert.Contains(blocks, b => b.Type == BlockType.agent_message && b.Content == "SUMMARY" && b.Compressed);
        Assert.DoesNotContain(blocks, b => b.Content == "answer 0"); // old turn compacted
    }
// ── Reasoning compression ──

    [Fact]
    public async Task ReasoningCompression_CompactsLargeReasoningBlock()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions(compression: new CompressionOptions
        {
            AutoCompress = false,
            AutoCompressReasoning = true,
            AutoCompressReasoningMaxSize = 100,
        });
        var reasoningBlock = MemoryBlock.AgentReasoning(new string('x', 500));
        reasoningBlock.Number = 1;
        await _db.Blocks.AppendBlocksAsync(AgentId, [reasoningBlock], nextNumber: 2);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "SHORT")]));
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi")])]);

        var events = await RunTurnAsync();

        Assert.Contains(events, e => e is AutoToolTurnEvent { Name: "compress_reasoning" });

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var reasoning = blocks.First(b => b.Type == BlockType.agent_reasoning);
        Assert.Equal("SHORT", reasoning.Content);
    }
}
