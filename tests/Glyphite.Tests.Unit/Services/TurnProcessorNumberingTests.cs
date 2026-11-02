using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Host.Memory;
using Glyphite.Host.Services;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

/// <summary>
/// Block numbering across a real <see cref="TurnProcessor"/> turn.
///
/// <see cref="BlockMemoryProvider.BuildContextAsync"/> creates the <c>agent_data</c> block (#1) on a
/// brand-new session, so the turn's own block numbers must be read *after* the context is built.
/// Reading <c>next_number</c> earlier handed the first <c>user_message</c> the same number as
/// <c>agent_data</c>, which broke the ordering key compaction relies on (and left a stray
/// first-turn block behind after compaction).
/// </summary>
public class TurnProcessorNumberingTests : IDisposable
{
    private const string AgentId = "test-agent";

    private readonly TestDb _db = new();
    private readonly FakeChatClient _chat = new();
    private readonly IConfigService _cfg = Substitute.For<IConfigService>();
    private readonly IToolRegistry _tools = Substitute.For<IToolRegistry>();
    private readonly ISessionConfigLoader _configLoader = Substitute.For<ISessionConfigLoader>();
    private readonly IInstructionProvider _instructions = Substitute.For<IInstructionProvider>();
    private readonly CompactionService _compaction;

    public TurnProcessorNumberingTests()
    {
        _compaction = new CompactionService(_db.Blocks, _db.Sessions, _cfg, _chat, NullLogger<CompactionService>.Instance);

        _tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>([]));
        _configLoader.LoadConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        _instructions.BuildInstructionsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult("test instructions"));

        _cfg.GetOptionsAsync<LlmOptions>(LlmOptions.Section, Arg.Any<string?>()).Returns(new LlmOptions
        {
            Endpoint = "http://localhost",
            ApiKey = "test-key",
            Model = "test-model",
            ContextWindow = 100_000,
            Models = [new LlmModel { Name = "test-model" }],
        });
        _cfg.GetOptionsAsync<AgentOptions>(AgentOptions.Section, Arg.Any<string?>())
            .Returns(new AgentOptions { MaxToolIterations = 5, AgentName = AgentId });
        _cfg.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, Arg.Any<string?>())
            .Returns(new CompressionOptions { AutoCompress = false, AutoCompressReasoning = false });
        _cfg.GetOptionsAsync<ImageOptions>(ImageOptions.Section, Arg.Any<string?>())
            .Returns(new ImageOptions { Enabled = false });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task FirstTurn_FreshSession_BlockNumbersAreUnique()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);

        // A real provider (not a substitute): building the context is what creates agent_data.
        var memory = new BlockMemoryProvider(
            _db.Sessions, _db.Blocks, _cfg,
            new MemoryOptions(), new AgentOptions { MaxToolIterations = 5, AgentName = AgentId },
            "test-model", null);

        var processor = new TurnProcessor(
            _db.Sessions, _db.Blocks, memory, _chat, _tools, _cfg,
            _compaction, _configLoader, _instructions,
            new ImageLoader(), new ImageAttachmentSink(), NullLogger<TurnProcessor>.Instance);

        _chat.QueueStream([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("hi")])]);
        await foreach (var _ in processor.ProcessAsync(AgentId, "hello", new ChatOptions(), default))
        {
        }

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);

        // P0 symptom this guards against: agent_data and the first user_message shared number 1.
        Assert.Equal(blocks.Count, blocks.Select(b => b.Number).Distinct().Count());
        Assert.Equal(BlockType.agent_data, blocks[0].Type);
        Assert.Equal(1, blocks[0].Number);

        var user = Assert.Single(blocks, b => b.Type == BlockType.user_message);
        Assert.Equal(2, user.Number);
        Assert.Equal("hello", user.Content);

        // agent_data, user, assistant text, turn marker
        Assert.Equal(4, blocks.Count);
        Assert.Equal(BlockType.turn, blocks[^1].Type);
        Assert.Equal([1.0, 2.0, 3.0, 4.0], blocks.Select(b => b.Number));
    }

    [Fact]
    public async Task ToolWritingBlockMidTurn_DoesNotCollideWithTurnBlocks()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);

        // A tool that writes its own block, the way the todo tool does. It knows nothing about
        // numbering: it appends, and the store hands out a free number.
        Func<Task<string>> writeBlock = async () =>
        {
            await _db.Blocks.AppendBlocksAsync(AgentId, [MemoryBlock.Create(BlockType.todo, "from-tool")]);
            return "written";
        };
        _tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>([AIFunctionFactory.Create(writeBlock, "write_block")]));

        var memory = new BlockMemoryProvider(
            _db.Sessions, _db.Blocks, _cfg,
            new MemoryOptions(), new AgentOptions { MaxToolIterations = 5, AgentName = AgentId },
            "test-model", null);

        var processor = new TurnProcessor(
            _db.Sessions, _db.Blocks, memory, _chat, _tools, _cfg,
            _compaction, _configLoader, _instructions,
            new ImageLoader(), new ImageAttachmentSink(), NullLogger<TurnProcessor>.Instance);

        _chat.QueueStream(
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "write_block", new Dictionary<string, object?>())])],
            [new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])]);

        await foreach (var _ in processor.ProcessAsync(AgentId, "do it", new ChatOptions(), default))
        {
        }

        var blocks = await _db.Blocks.LoadBlocksAsync(AgentId);

        // Regression: the turn carried its own in-memory counter, so the block a tool wrote took a
        // number the turn then handed out again (todo#29 + tool#29 in a real session).
        Assert.Equal(blocks.Count, blocks.Select(b => b.Number).Distinct().Count());

        var toolBlock = Assert.Single(blocks, b => b.Type == BlockType.todo);
        var callBlock = Assert.Single(blocks, b => b.Type == BlockType.tool && b.ToolName == "write_block");
        Assert.NotEqual(callBlock.Number, toolBlock.Number);
        Assert.Equal(BlockType.turn, blocks[^1].Type);
    }
}