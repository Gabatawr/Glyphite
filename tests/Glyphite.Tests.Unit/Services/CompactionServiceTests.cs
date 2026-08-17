using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class CompactionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IConfigService _cfg = Substitute.For<IConfigService>();
    private readonly FakeChatClient _chat = new();
    private readonly CompactionService _svc;

    public CompactionServiceTests()
    {
        _svc = new CompactionService(_db.Blocks, _db.Sessions, _cfg, _chat, NullLogger<CompactionService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private void SetupCompression(CompressionOptions opts)
        => _cfg.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, Arg.Any<string?>()).Returns(opts);

    private static MemoryBlock WithNumber(MemoryBlock block, double number)
    {
        block.Number = number;
        return block;
    }

    private static readonly CompressionOptions DefaultOpts = new() { AutoCompress = true, AutoThreshold = 50 };

    // ── PickStrategy ──

    [Fact]
    public void PickStrategy_OnlyFiboEnabled_ReturnsFibo()
    {
        var opts = new CompressionOptions { Strategies = new() { ["fibo"] = true, ["struct"] = false } };
        Assert.Equal("fibo", CompactionService.PickStrategy(opts));
    }

    [Fact]
    public void PickStrategy_MultipleEnabled_ReturnsEnabledOne()
    {
        var opts = new CompressionOptions { Strategies = new() { ["fibo"] = true, ["struct"] = true } };
        for (var i = 0; i < 20; i++)
            Assert.Contains(CompactionService.PickStrategy(opts), new[] { "fibo", "struct" });
    }

    // ── EvaluateCompactionStatusAsync ──

    [Fact]
    public async Task Evaluate_AutoCompressOff_WillCompactFalse()
    {
        SetupCompression(new CompressionOptions { AutoCompress = false });

        var status = await _svc.EvaluateCompactionStatusAsync("agent", 100_000);

        Assert.False(status.IsThresholdExceeded);
        Assert.False(status.WillCompact);
    }

    [Fact]
    public async Task Evaluate_NoUsage_BelowThreshold()
    {
        await _db.Sessions.EnsureSessionAsync("agent");
        SetupCompression(DefaultOpts);

        var status = await _svc.EvaluateCompactionStatusAsync("agent", 10_000);

        Assert.False(status.IsThresholdExceeded);
        Assert.False(status.WillCompact);
    }

    [Fact]
    public async Task Evaluate_AboveThreshold_WithOldTurns_WillCompact_SoftMode()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        await _db.Sessions.RecordUsageAsync(agentId, 0, 0, 0, lastRequestHit: 0, lastRequestMiss: 6_000);
        SetupCompression(DefaultOpts);

        var status = await _svc.EvaluateCompactionStatusAsync(agentId, 10_000);

        Assert.True(status.IsThresholdExceeded);
        Assert.True(status.WillCompact);
        Assert.Equal("soft", status.Mode);
    }

    // ── GroupByTurns ──

    [Fact]
    public void GroupByTurns_AgentDataGroup_ThenTurnGroups()
    {
        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.AgentData("k", "v"), 1),
            WithNumber(MemoryBlock.TurnMarker(), 2),
            WithNumber(MemoryBlock.UserMessage("q1"), 3),
            WithNumber(MemoryBlock.AgentMessage("a1"), 4),
            WithNumber(MemoryBlock.TurnMarker(), 5),
        };

        var groups = CompactionService.GroupByTurns(blocks);

        Assert.Equal(2, groups.Count);
        Assert.Equal(BlockType.agent_data, groups[0][0].Type);
        Assert.Equal(BlockType.user_message, groups[1][0].Type);
    }

    // ── Fibo strategy ──

    [Fact]
    public async Task FiboCompact_SummarizesOldZones_PreservesSafeTurns()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "SUMMARY")]));

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        var result = await FiboStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        Assert.True(result);
        var after = await _db.Blocks.LoadBlocksAsync(agentId);
        // agent_data + summary + turn + 2 safe turns (6 blocks) = 9
        Assert.Equal(9, after.Count);
        Assert.Contains(after, b => b.Type == BlockType.agent_message && b.Content == "SUMMARY" && b.Compressed);
        Assert.DoesNotContain(after, b => b.Content == "answer 0"); // summarized away
        Assert.Contains(after, b => b.Content == "answer 1");
        Assert.Contains(after, b => b.Content == "answer 2");
    }

    [Fact]
    public async Task FiboCompact_SummaryFails_BlocksFallBackIntact()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        _chat.ThrowOnResponse = true;

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        var result = await FiboStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        Assert.True(result);
        var after = await _db.Blocks.LoadBlocksAsync(agentId);
        Assert.Equal(10, after.Count); // nothing lost
        Assert.Contains(after, b => b.Content == "answer 0");
        Assert.Contains(after, b => b.Content == "answer 1");
        Assert.Contains(after, b => b.Content == "answer 2");
    }

    // ── Struct strategy ──

    [Fact]
    public async Task StructCompact_SummaryReplacesOldTurns()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "STRUCT SUMMARY")]));

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        var result = await StructStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        Assert.True(result);
        var after = await _db.Blocks.LoadBlocksAsync(agentId);
        // agent_data + 2 safe turns (6) + summary + turn = 9
        Assert.Equal(9, after.Count);
        Assert.Contains(after, b => b.Content == "STRUCT SUMMARY" && b.Compressed);
        Assert.DoesNotContain(after, b => b.Content == "answer 0");
        Assert.Contains(after, b => b.Content == "answer 1");
    }

    [Fact]
    public async Task StructCompact_SummaryFails_BlocksFallBackIntact()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        _chat.ThrowOnResponse = true;

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        var result = await StructStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        Assert.True(result);
        var after = await _db.Blocks.LoadBlocksAsync(agentId);
        Assert.Equal(10, after.Count); // nothing lost: agent_data + all 3 turns intact
        Assert.Contains(after, b => b.Content == "answer 0");
        Assert.Contains(after, b => b.Content == "answer 1");
        Assert.Contains(after, b => b.Content == "answer 2");
    }
}
