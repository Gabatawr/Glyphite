using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Memory;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Memory;

public class BlockMemoryProviderTests
{
    private const string AgentId = "test-agent";

    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IBlockStore _blocks = Substitute.For<IBlockStore>();
    private readonly IConfigService _cfg = Substitute.For<IConfigService>();
    private readonly AgentOptions _agentOpts = new() { AgentName = "test-agent", MaxToolIterations = 5 };

    private BlockMemoryProvider CreateProvider(string? defaultModel = null)
        => new(_agents, _blocks, _cfg, new MemoryOptions(), _agentOpts, defaultModel);

    // ── Context building ──

    [Fact]
    public async Task BuildContext_EmptyStore_CreatesAgentDataBlock()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([]);

        var messages = await CreateProvider().BuildContextAsync(AgentId);

        var message = Assert.Single(messages);
        Assert.Contains("agent: test-agent", message.Text);
        await _agents.Received(1).EnsureSessionAsync(AgentId);
        await _blocks.Received(1).AppendBlocksAsync(
            AgentId,
            Arg.Is<List<MemoryBlock>>(l => l.Count == 1 && l[0].Type == BlockType.agent_data && l[0].Number == 1),
            2);
    }

    [Fact]
    public async Task BuildContext_ExcludesTurnAndAutoToolBlocks()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([
            Block(BlockType.user_message, 1, "question"),
            Block(BlockType.turn, 2, ""),
            Block(BlockType.agent_message, 3, "answer"),
            Block(BlockType.auto_tool, 4, "auto content"),
        ]);

        var messages = await CreateProvider().BuildContextAsync(AgentId);

        Assert.Equal(2, messages.Count);
        var text = string.Join("\n", messages.Select(m => m.Text));
        Assert.Contains("question", text);
        Assert.Contains("answer", text);
        Assert.DoesNotContain("auto content", text);
        await _blocks.DidNotReceive().AppendBlocksAsync(Arg.Any<string>(), Arg.Any<List<MemoryBlock>>(), Arg.Any<double>());
    }

    [Fact]
    public async Task BuildContext_AgentNameChanged_UpdatesAgentDataWithModel()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([Block(BlockType.agent_data, 1, "agent: old-name")]);
        var provider = new BlockMemoryProvider(_agents, _blocks, _cfg, new MemoryOptions(),
            new AgentOptions { AgentName = "new-name", MaxToolIterations = 5 }, defaultModel: null);

        await provider.BuildContextAsync(AgentId, model: "gpt-4");

        await _blocks.Received(1).UpdateBlockAsync(
            AgentId, 1, "agent: new-name",
            Arg.Is<Dictionary<string, object>>(d => (string)d["model"] == "gpt-4"),
            "gpt-4");
    }

    [Fact]
    public async Task BuildContext_ContentUnchanged_NoBlockUpdate()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([Block(BlockType.agent_data, 1, "agent: test-agent")]);

        await CreateProvider().BuildContextAsync(AgentId, model: "gpt-4");

        await _blocks.DidNotReceive().UpdateBlockAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task BuildContext_UsesAgentStoreModel_WhenModelNotPassed()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([Block(BlockType.agent_data, 1, "agent: old-name")]);
        _agents.GetAgentModelAsync(AgentId).Returns("stored-model");
        var provider = new BlockMemoryProvider(_agents, _blocks, _cfg, new MemoryOptions(),
            new AgentOptions { AgentName = "new-name", MaxToolIterations = 5 }, defaultModel: null);

        await provider.BuildContextAsync(AgentId);

        await _blocks.Received(1).UpdateBlockAsync(AgentId, 1, "agent: new-name", Arg.Any<Dictionary<string, object>>(), "stored-model");
    }

    // ── Error storage ──

    [Fact]
    public async Task StoreError_AppendsSystemErrorBlock()
    {
        _agents.GetNextNumberAsync(AgentId).Returns(7);

        await CreateProvider().StoreErrorAsync(AgentId, "boom");

        await _blocks.Received(1).AppendBlocksAsync(
            AgentId,
            Arg.Is<List<MemoryBlock>>(l => l.Count == 1 && l[0].Type == BlockType.system_error && l[0].Number == 7 && l[0].Content == "boom"),
            8);
    }

    private static MemoryBlock Block(BlockType type, double number, string content)
    {
        var block = MemoryBlock.Create(type, content);
        block.Number = number;
        return block;
    }
}
