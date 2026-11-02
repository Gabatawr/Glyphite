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
    public void GroupByTurns_ProductionShape_AgentDataIsOwnLeadingGroup()
    {
        // Exactly what TurnProcessor writes: agent_data, then the first turn WITHOUT a leading
        // turn marker. The first turn must be its own group — not absorbed by the agent_data header.
        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.AgentData("k", "v"), 1),
            WithNumber(MemoryBlock.UserMessage("q0"), 2),
            WithNumber(MemoryBlock.AgentMessage("a0"), 3),
            WithNumber(MemoryBlock.TurnMarker(), 4),
            WithNumber(MemoryBlock.UserMessage("q1"), 5),
            WithNumber(MemoryBlock.AgentMessage("a1"), 6),
            WithNumber(MemoryBlock.TurnMarker(), 7),
        };

        var groups = CompactionService.GroupByTurns(blocks);

        Assert.Equal(3, groups.Count);
        Assert.All(groups[0], b => Assert.Equal(BlockType.agent_data, b.Type));
        Assert.Equal([BlockType.user_message, BlockType.agent_message, BlockType.turn],
            groups[1].Select(b => b.Type));
        Assert.Equal("q0", groups[1][0].Content);
        Assert.Equal("q1", groups[2][0].Content);
    }

    [Fact]
    public void GroupByTurns_NoAgentData_FirstTurnIsItsOwnGroup()
    {
        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.UserMessage("q0"), 1),
            WithNumber(MemoryBlock.AgentMessage("a0"), 2),
            WithNumber(MemoryBlock.TurnMarker(), 3),
            WithNumber(MemoryBlock.UserMessage("q1"), 4),
            WithNumber(MemoryBlock.AgentMessage("a1"), 5),
            WithNumber(MemoryBlock.TurnMarker(), 6),
        };

        var groups = CompactionService.GroupByTurns(blocks);

        Assert.Equal(2, groups.Count);
        Assert.Equal("q0", groups[0][0].Content);
        Assert.Equal("q1", groups[1][0].Content);
    }

    [Fact]
    public void GroupByTurns_TrailingTurnWithoutMarker_KeptAsGroup()
    {
        // A turn that never completed (crash, Escape before FinalizeAsync) has no turn marker.
        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.AgentData("k", "v"), 1),
            WithNumber(MemoryBlock.TurnMarker(), 2),
            WithNumber(MemoryBlock.UserMessage("q-open"), 3),
            WithNumber(MemoryBlock.AgentMessage("a-open"), 4),
        };

        var groups = CompactionService.GroupByTurns(blocks);

        Assert.Equal(3, groups.Count);
        Assert.Equal("q-open", groups[^1][0].Content);
    }

    // ── ClassifyZones ──

    [Fact]
    public void ClassifyZones_ProductionShape_KeepsTwoNewestAndCompressesTheRest()
    {
        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.AgentData("k", "v"), 1),
            WithNumber(MemoryBlock.UserMessage("q0"), 2),
            WithNumber(MemoryBlock.TurnMarker(), 3),
            WithNumber(MemoryBlock.UserMessage("q1"), 4),
            WithNumber(MemoryBlock.TurnMarker(), 5),
            WithNumber(MemoryBlock.UserMessage("q2"), 6),
            WithNumber(MemoryBlock.TurnMarker(), 7),
        };

        var zones = CompactionService.ClassifyZones(CompactionService.GroupByTurns(blocks), threshold: 100_000);

        Assert.Equal(2, zones.SafeGroups.Count);
        Assert.Single(zones.ToCompressGroups);
        Assert.Equal("q0", zones.ToCompressGroups[0][0].Content); // the FIRST turn is compressible
    }

    [Fact]
    public void ClassifyZones_HistoryWithoutAgentData_ClassifiesTheOldestGroup()
    {
        // Regression: index 0 used to be skipped blind, so a history without agent_data
        // never had its oldest turn classified — and compaction then dropped it.
        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.UserMessage("q0"), 1),
            WithNumber(MemoryBlock.TurnMarker(), 2),
            WithNumber(MemoryBlock.UserMessage("q1"), 3),
            WithNumber(MemoryBlock.TurnMarker(), 4),
            WithNumber(MemoryBlock.UserMessage("q2"), 5),
            WithNumber(MemoryBlock.TurnMarker(), 6),
        };

        var zones = CompactionService.ClassifyZones(CompactionService.GroupByTurns(blocks), threshold: 100_000);

        Assert.Equal(2, zones.SafeGroups.Count);
        Assert.Single(zones.ToCompressGroups);
        Assert.Equal("q0", zones.ToCompressGroups[0][0].Content);
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
        // ...and covered by the summary: the summarizer received the whole first turn.
        var promptText = string.Concat(_chat.ReceivedMessages.SelectMany(m => m.Select(x => x.Text ?? "")));
        Assert.Contains("question 0", promptText);
        Assert.Contains("answer 0", promptText);
        Assert.Contains(after, b => b.Content == "answer 1");
        Assert.Contains(after, b => b.Content == "answer 2");
        AssertUniqueNumbers(after);
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
        Assert.Contains(after, b => b.Content == "question 0");
        Assert.Contains(after, b => b.Content == "answer 0");
        Assert.Contains(after, b => b.Content == "answer 1");
        Assert.Contains(after, b => b.Content == "answer 2");
        AssertUniqueNumbers(after);
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
        // Struct feeds the whole history to the summarizer, so the first turn is covered too.
        var promptText = string.Concat(_chat.ReceivedMessages.SelectMany(m => m.Select(x => x.Text ?? "")));
        Assert.Contains("question 0", promptText);
        Assert.Contains(after, b => b.Content == "answer 1");
        AssertUniqueNumbers(after);
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
        Assert.Contains(after, b => b.Content == "question 0");
        Assert.Contains(after, b => b.Content == "answer 0");
        Assert.Contains(after, b => b.Content == "answer 1");
        Assert.Contains(after, b => b.Content == "answer 2");
        AssertUniqueNumbers(after);
    }

    /// <summary>Numbers are the ordering key for the whole transcript — duplicates break it.</summary>
    private static void AssertUniqueNumbers(List<MemoryBlock> blocks)
        => Assert.Equal(blocks.Count, blocks.Select(b => b.Number).Distinct().Count());
}
