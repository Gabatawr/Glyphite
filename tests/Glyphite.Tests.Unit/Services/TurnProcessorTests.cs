using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
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
        _tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>([]));
        _configLoader.LoadConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        _instructions.BuildInstructionsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult("test instructions"));
    }

    public void Dispose() => _db.Dispose();

    private TurnProcessor CreateProcessor(ImageAttachmentSink? images = null) => new(
        _db.Sessions, _db.Blocks, _memory, _chat, _tools, _cfg,
        _compaction, _configLoader, _instructions,
        new ImageLoader(), images ?? new ImageAttachmentSink(),
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

    /// <summary>
    /// The tools a turn hands the model must resolve relative paths against the agent's own directory
    /// (a subagent's cwd), not the directory the process happens to run in.
    /// </summary>
    [Fact]
    public async Task AgentCwd_IsTheBaseDirectoryOfTheTools()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi")])]);

        var processor = CreateProcessor();
        await foreach (var _ in processor.ProcessAsync(
            AgentId, "hello", new ChatOptions(), default, agentCwd: "/tmp/some-agent-dir"))
        {
        }

        await _tools.Received(1).GetBuiltinToolsAsync(AgentId, Arg.Any<bool>(), "/tmp/some-agent-dir");
    }

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

    [Fact]
    public async Task TurnMarker_SeparatesBillingOutputFromContextSize()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        using var doc = JsonDocument.Parse(
            """{"usage":{"prompt_tokens":500,"completion_tokens":60,"completion_tokens_details":{"reasoning_tokens":45}}}""");
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi")]) { RawRepresentation = doc.RootElement }]);

        await RunTurnAsync();

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var marker = Assert.Single(blocks, b => b.Type == BlockType.turn);
        using var parsed = JsonDocument.Parse(marker.Content);
        var root = parsed.RootElement;

        Assert.Equal(500, root.GetProperty("miss").GetInt64());       // prompt = the only context measure
        Assert.Equal(60, root.GetProperty("out_").GetInt64());        // billed output, reasoning inside
        Assert.Equal(45, root.GetProperty("reasoning").GetInt64());   // ...of which hidden thinking

        // `stored*` answers a different question: how much of this turn actually sits in the
        // history. It is measured from the blocks (same ruler as compaction), so it can never be
        // confused with the bill.
        var modelOutput = blocks
            .Where(b => b.Type is BlockType.agent_message or BlockType.agent_reasoning)
            .ToList();
        Assert.Equal(CompactionService.EstimateZoneTokens(modelOutput), root.GetProperty("stored").GetInt64());
        Assert.Equal(0, root.GetProperty("stored_reasoning").GetInt64()); // no reasoning in this response
        Assert.True(root.GetProperty("stored").GetInt64() < root.GetProperty("out_").GetInt64());
    }

    [Fact]
    public async Task TurnMarker_StoredReasoning_IsMeasuredFromTheReasoningBlock()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _chat.QueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(new string('t', 400))]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(new string('a', 200))]),
        ]);

        await RunTurnAsync();

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var marker = Assert.Single(blocks, b => b.Type == BlockType.turn);
        using var parsed = JsonDocument.Parse(marker.Content);
        var root = parsed.RootElement;

        var reasoningSize = CompactionService.EstimateZoneTokens(
            blocks.Where(b => b.Type == BlockType.agent_reasoning).ToList());
        var textSize = CompactionService.EstimateZoneTokens(
            blocks.Where(b => b.Type == BlockType.agent_message).ToList());

        Assert.True(reasoningSize > 0);
        Assert.Equal(reasoningSize, root.GetProperty("stored_reasoning").GetInt64());
        Assert.Equal(textSize, root.GetProperty("stored_text").GetInt64());
        // Each part floors its own division by 4, so the parts can lag the total by a token or two.
        Assert.InRange(root.GetProperty("stored").GetInt64(), reasoningSize + textSize - 2, reasoningSize + textSize);
    }

    [Fact]
    public async Task TurnMarker_StoredParts_AddUpToTheFootprintInHistory()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();

        // A real tool, so the turn writes a tool block with a result in it.
        var bash = AIFunctionFactory.Create((string command) => Task.FromResult("ok"), "bash");
        _tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>([bash]));

        // Iteration 1: thinking + a tool call. Iteration 2: thinking + the answer.
        _chat.QueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(new string('t', 200))]),
            new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("c1", "bash", new Dictionary<string, object?> { ["command"] = "ls" })]),
        ]);
        _chat.QueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(new string('u', 100))]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]),
        ]);

        await RunTurnAsync();

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var marker = Assert.Single(blocks, b => b.Type == BlockType.turn);
        using var parsed = JsonDocument.Parse(marker.Content);
        var root = parsed.RootElement;

        var parts = root.GetProperty("stored_reasoning").GetInt64()
                  + root.GetProperty("stored_text").GetInt64()
                  + root.GetProperty("stored_tool").GetInt64();
        Assert.True(root.GetProperty("stored_reasoning").GetInt64() > 0);
        Assert.True(root.GetProperty("stored_text").GetInt64() > 0);
        Assert.True(root.GetProperty("stored_tool").GetInt64() > 0);
        // Per-part flooring: the three parts add up to `stored` within a token or two.
        var stored = root.GetProperty("stored").GetInt64();
        Assert.InRange(parts, stored - 2, stored);

        // The recorded footprint is the footprint of the blocks that were actually persisted.
        var modelOutput = blocks
            .Where(b => b.Type is BlockType.agent_message or BlockType.agent_reasoning or BlockType.tool)
            .ToList();
        Assert.Equal(CompactionService.EstimateZoneTokens(modelOutput), root.GetProperty("stored").GetInt64());
    }

    [Fact]
    public async Task TurnMarker_ContextIsTheLastIterationPrompt_NotTheSumOverIterations()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();

        var bash = AIFunctionFactory.Create((string command) => Task.FromResult("ok"), "bash");
        _tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>([bash]));

        // Two iterations, each with its own prompt: 1000 tokens, then 1200 (600 of them cached).
        using var first = JsonDocument.Parse(
            """{"usage":{"prompt_tokens":1000,"prompt_tokens_details":{"cached_tokens":400},"completion_tokens":50}}""");
        using var second = JsonDocument.Parse(
            """{"usage":{"prompt_tokens":1200,"prompt_tokens_details":{"cached_tokens":600},"completion_tokens":30}}""");

        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("c1", "bash", new Dictionary<string, object?> { ["command"] = "ls" })])
            { RawRepresentation = first.RootElement }]);
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
            { RawRepresentation = second.RootElement }]);

        await RunTurnAsync();

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var marker = Assert.Single(blocks, b => b.Type == BlockType.turn);
        using var parsed = JsonDocument.Parse(marker.Content);
        var root = parsed.RootElement;

        // Billing is summed over the turn's iterations: 1000 + 1200 prompt tokens.
        Assert.Equal(2200, root.GetProperty("hit").GetInt64() + root.GetProperty("miss").GetInt64());
        Assert.Equal(80, root.GetProperty("out_").GetInt64());

        // Context is the prompt of the last iteration only — the number the prompt counter shows and
        // the compaction threshold reads. Reading `hit`/`miss` as "context" would double it.
        Assert.Equal(1200, root.GetProperty("context").GetInt64());
        Assert.True(root.GetProperty("context").GetInt64()
                    < root.GetProperty("hit").GetInt64() + root.GetProperty("miss").GetInt64());
    }
// ── Images ──

    [Fact]
    public async Task ImagePathInMessage_IsAttachedToTheUserMessage_AndMarkedInTheBlock()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("I see")])]);

        using var dir = new TempDir();
        var imagePath = dir.Write("shot.png", TestImages.Png(320, 200));

        var events = await RunTurnAsync($"what is on {imagePath}?");

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

        var events = await RunTurnAsync("look at /nowhere/gone.png");

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

        var events = await RunTurnAsync($"what is on {imagePath}?");

        Assert.DoesNotContain(events, e => e is ImageAttachedTurnEvent);
        var sent = _chat.ReceivedMessages[0];
        Assert.DoesNotContain(sent.SelectMany(m => m.Contents).OfType<DataContent>(), _ => true);
    }

    [Fact]
    public async Task ImageAlreadyInTheMessage_IsNotSentTwice_WhenTheModelAsksForIt()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions();

        using var dir = new TempDir();
        var imagePath = dir.Write("shot.png", TestImages.Png(320, 200));

        // The real tool, sharing the sink the turn seeds with its own attachments.
        var images = new ImageAttachmentSink();
        _tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>(
                [ImageTool.AsViewImageFunction(new ImageLoader(), images, _cfg)]));

        // Iteration 1: the model asks view_image for the very picture the turn already attached.
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("c1", "view_image",
                new Dictionary<string, object?> { ["source"] = imagePath })])]);
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])]);

        await foreach (var _ in CreateProcessor(images).ProcessAsync(
            AgentId, $"what is on {imagePath}?", new ChatOptions(), default))
        {
        }

        // One picture reached the provider — not two copies of the same one.
        var lastCall = _chat.ReceivedMessages.Last();
        Assert.Single(lastCall.SelectMany(m => m.Contents).OfType<DataContent>());

        // And the tool said so instead of queueing it again.
        var toolText = lastCall.Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents).OfType<TextContent>().Select(t => t.Text);
        Assert.Contains(toolText, t => t!.Contains("Already attached"));
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

        // Replacing history mid-turn must not punch a hole in the numbering: the auto_tool block
        // and the turn's own blocks continue from where the compaction left off.
        Assert.Equal(Enumerable.Range(1, blocks.Count).Select(i => (double)i), blocks.Select(b => b.Number));
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
        await _db.Blocks.AppendBlocksAsync(AgentId, [reasoningBlock]);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "SHORT")]));
        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi")])]);

        var events = await RunTurnAsync();

        Assert.Contains(events, e => e is AutoToolTurnEvent { Name: "compress_reasoning" });

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        var reasoning = blocks.First(b => b.Type == BlockType.agent_reasoning);
        Assert.Equal("SHORT", reasoning.Content);
    }

    /// <summary>
    /// Reasoning is kept as history on purpose, so compression is its only brake — but the newest
    /// turn must survive: it is the model's working memory for the next step.
    /// </summary>
    [Fact]
    public async Task ReasoningCompression_SkipsTheNewestTurn()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions(compression: new CompressionOptions
        {
            AutoCompress = false,
            AutoCompressReasoning = true,
            AutoCompressReasoningMaxSize = 100,
        });

        var oldReasoning = MemoryBlock.AgentReasoning(new string('o', 500));
        var newReasoning = MemoryBlock.AgentReasoning(new string('n', 500));
        await _db.Blocks.AppendBlocksAsync(AgentId,
        [
            MemoryBlock.UserMessage("q0"),
            oldReasoning,
            MemoryBlock.TurnMarker(),
            MemoryBlock.UserMessage("q1"),
            newReasoning,
        ]);
        // Two summaries are queued, only one may be consumed: a second summarization call would
        // mean the newest turn's reasoning was rewritten — exactly what must not happen.
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "OLD-SUMMARY")]));
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "NEW-SUMMARY")]));

        var events = await RunTurnAsync();

        var autoTool = Assert.Single(events.OfType<AutoToolTurnEvent>(), e => e.Name == "compress_reasoning");
        Assert.Contains("\"count\":1", autoTool.Args);

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        Assert.Equal("OLD-SUMMARY", blocks.First(b => b.Number == oldReasoning.Number).Content);
        Assert.Equal(new string('n', 500), blocks.First(b => b.Number == newReasoning.Number).Content);
        Assert.Equal(1, _chat.ResponseCallCount);
    }

    /// <summary>A subagent's turns open with <c>agent_task</c>, not <c>user_message</c> — the boundary must follow either.</summary>
    [Fact]
    public async Task ReasoningCompression_UsesAgentTaskAsTheNewestTurnBoundary()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        SetupOptions(compression: new CompressionOptions
        {
            AutoCompress = false,
            AutoCompressReasoning = true,
            AutoCompressReasoningMaxSize = 100,
        });

        var oldReasoning = MemoryBlock.AgentReasoning(new string('o', 500));
        var newReasoning = MemoryBlock.AgentReasoning(new string('n', 500));
        await _db.Blocks.AppendBlocksAsync(AgentId,
        [
            MemoryBlock.AgentTask("task 0"),
            oldReasoning,
            MemoryBlock.TurnMarker(),
            MemoryBlock.AgentTask("task 1"),
            newReasoning,
        ]);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "OLD-SUMMARY")]));
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "NEW-SUMMARY")]));

        await RunTurnAsync();

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);
        Assert.Equal("OLD-SUMMARY", blocks.First(b => b.Number == oldReasoning.Number).Content);
        Assert.Equal(new string('n', 500), blocks.First(b => b.Number == newReasoning.Number).Content);
    }
}