using System.Text.Json;
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

    [Fact]
    public async Task Evaluate_BelowThreshold_DoesNotReadBlocks()
    {
        var blocks = Substitute.For<IBlockStore>();
        var agents = Substitute.For<IAgentStore>();
        agents.GetLastUsageAsync(Arg.Any<string>()).Returns((0L, 0L, 0L, 1_000L, 2_000L));
        var cfg = Substitute.For<IConfigService>();
        cfg.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, Arg.Any<string?>()).Returns(DefaultOpts);
        var svc = new CompactionService(blocks, agents, cfg, _chat, NullLogger<CompactionService>.Instance);

        var status = await svc.EvaluateCompactionStatusAsync("agent", contextWindow: 1_000_000);

        Assert.False(status.IsThresholdExceeded);
        Assert.False(status.WillCompact);
        Assert.Equal("none", status.Mode);
        // The auto path runs on every turn: one usage row answers "not yet", so the block table
        // (read + grouping + zone classification) must stay untouched.
        await blocks.DidNotReceive().LoadBlocksAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Evaluate_Manual_BelowThreshold_StillClassifiesZones()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        SetupCompression(DefaultOpts);

        // /compression is an explicit ask: it must report zones even when the auto threshold is not met.
        var status = await _svc.EvaluateCompactionStatusAsync(agentId, contextWindow: 1_000_000, isManual: true);

        Assert.False(status.IsThresholdExceeded);
        Assert.True(status.WillCompact);
        Assert.NotEqual("none", status.Mode);
    }

    [Fact]
    public async Task Evaluate_NoUsage_EstimatesFromHistoryInsteadOfSkipping()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        SetupCompression(DefaultOpts); // AutoThreshold = 50% of the window

        // No usage rows at all — exactly what a fresh or forked session looks like (ForkSessionAsync
        // copies blocks, not session_usage). The threshold must be judged against the history, not
        // against a zero that can never exceed anything.
        var status = await _svc.EvaluateCompactionStatusAsync(agentId, contextWindow: 100);

        Assert.True(status.IsThresholdExceeded);
        Assert.True(status.WillCompact);
        Assert.NotEqual("none", status.Mode);
    }

    [Fact]
    public async Task Evaluate_NoUsage_TinyHistory_StaysBelowThreshold()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await _db.Blocks.AppendBlocksAsync(agentId, [MemoryBlock.AgentData("agent", "probe")]);
        SetupCompression(DefaultOpts);

        // The estimate must not turn a one-block session into a compaction candidate.
        var status = await _svc.EvaluateCompactionStatusAsync(agentId, contextWindow: 1_000_000);

        Assert.False(status.IsThresholdExceeded);
        Assert.False(status.WillCompact);
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

    [Fact]
    public void ClassifyZones_HardMode_MeasuresSummarySize_NotGenerationTokens()
    {
        // A short summary whose turn marker claims a huge generation count — reasoning models report
        // their hidden thinking in that number. Hard mode exists to reclaim context, so it must look
        // at the space the summary occupies, not at how much work the model did writing it.
        var summary = MemoryBlock.AgentMessage("short old summary");
        summary.Compressed = true;

        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.AgentData("k", "v"), 1),
            WithNumber(summary, 2),
            WithNumber(MemoryBlock.TurnMarker("""{"hit":0,"miss":90000,"out_":90000}"""), 3),
            WithNumber(MemoryBlock.UserMessage("q1"), 4),
            WithNumber(MemoryBlock.TurnMarker(), 5),
            WithNumber(MemoryBlock.UserMessage("q2"), 6),
            WithNumber(MemoryBlock.TurnMarker(), 7),
            WithNumber(MemoryBlock.UserMessage("q3"), 8),
            WithNumber(MemoryBlock.TurnMarker(), 9),
        };

        var zones = CompactionService.ClassifyZones(CompactionService.GroupByTurns(blocks), threshold: 10_000);

        Assert.Single(zones.CompressedGroups);
        Assert.False(zones.IsHardMode); // 90k "generated" tokens vs ~30 tokens of actual summary
    }

    [Fact]
    public void ClassifyZones_HardMode_LargeCompressedHistory_IsRecompressed()
    {
        var summary = MemoryBlock.AgentMessage(new string('x', 120_000)); // ~30k tokens rendered
        summary.Compressed = true;

        var blocks = new List<MemoryBlock>
        {
            WithNumber(MemoryBlock.AgentData("k", "v"), 1),
            WithNumber(summary, 2),
            WithNumber(MemoryBlock.TurnMarker("""{"hit":0,"miss":1,"out_":1}"""), 3),
            WithNumber(MemoryBlock.UserMessage("q1"), 4),
            WithNumber(MemoryBlock.TurnMarker(), 5),
            WithNumber(MemoryBlock.UserMessage("q2"), 6),
            WithNumber(MemoryBlock.TurnMarker(), 7),
            WithNumber(MemoryBlock.UserMessage("q3"), 8),
            WithNumber(MemoryBlock.TurnMarker(), 9),
        };

        var zones = CompactionService.ClassifyZones(CompactionService.GroupByTurns(blocks), threshold: 10_000);

        Assert.True(zones.IsHardMode);
        Assert.Empty(zones.CompressedGroups);
        Assert.Contains(zones.ToCompressGroups, g => g.Any(b => b.Compressed));
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
    public async Task FiboSummaryMarker_RecordsSummarizerUsage_NotAFakeContextSize()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);

        // The summarizer's own usage: prompt 777 / output 25, of which 20 are hidden thinking.
        for (var i = 0; i < 3; i++)
        {
            using var doc = JsonDocument.Parse(
                """{"usage":{"prompt_tokens":777,"completion_tokens":25,"completion_tokens_details":{"reasoning_tokens":20}}}""");
            _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, $"SUMMARY {i}")])
            {
                RawRepresentation = doc.RootElement.Clone(),
            });
        }

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        var result = await FiboStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        Assert.True(result);
        var after = await _db.Blocks.LoadBlocksAsync(agentId);

        // Safe-zone markers keep their original (empty) content; only freshly written summary
        // markers carry usage, so `out_` in the JSON marks exactly the ones under test.
        var summaryMarkers = after.Where(b => b.Type == BlockType.turn && b.Content.Contains("out_")).ToList();
        Assert.NotEmpty(summaryMarkers);

        foreach (var marker in summaryMarkers)
        {
            using var parsed = JsonDocument.Parse(marker.Content);
            var root = parsed.RootElement;

            // `miss` is the summarization prompt — previously it was overwritten with the output
            // count, which made the marker lie about input size.
            Assert.Equal(777, root.GetProperty("miss").GetInt64());
            Assert.Equal(25, root.GetProperty("out_").GetInt64());
            Assert.Equal(20, root.GetProperty("reasoning").GetInt64());

            // `stored*` is the size of what was kept: the summary block itself (markers are written
            // right after their summary). A summary is result text, so nothing lands in reasoning.
            var stored = root.GetProperty("stored").GetInt64();
            var summaryBlock = after.First(b => b.Number == marker.Number - 1);
            Assert.Equal(CompactionService.EstimateZoneTokens([summaryBlock]), stored);
            Assert.Equal(stored, root.GetProperty("stored_text").GetInt64());
            Assert.Equal(0, root.GetProperty("stored_reasoning").GetInt64());
            Assert.NotEqual(root.GetProperty("out_").GetInt64(), stored); // the two rulers differ
        }
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

        // The single marker carries the summarizer's billed usage plus the summary's stored size.
        var summaryMarker = Assert.Single(after, b => b.Type == BlockType.turn && b.Content.Contains("out_"));
        using var markerDoc = JsonDocument.Parse(summaryMarker.Content);
        var summaryBlock = after.First(b => b.Number == summaryMarker.Number - 1);
        Assert.Equal(CompactionService.EstimateZoneTokens([summaryBlock]),
            markerDoc.RootElement.GetProperty("stored").GetInt64());
        Assert.Equal(0, markerDoc.RootElement.GetProperty("stored_reasoning").GetInt64());

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

    [Fact]
    public async Task StructCompact_SummaryFails_KeepsChronologicalOrder()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 3);
        _chat.ThrowOnResponse = true;

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        await StructStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        var contents = (await _db.Blocks.LoadBlocksAsync(agentId)).Select(b => b.Content).ToList();
        var t1 = contents.IndexOf("question 0");
        var t2 = contents.IndexOf("question 1");
        var t3 = contents.IndexOf("question 2");

        // Fallback keeps the original blocks, so their relative order must survive too —
        // otherwise the next compaction reads a reordered transcript as if it were real.
        Assert.InRange(t1, 0, contents.Count - 1);
        Assert.InRange(t2, 0, contents.Count - 1);
        Assert.InRange(t3, 0, contents.Count - 1);
        Assert.True(t1 < t2, $"turn 1 must stay before turn 2 (was {t1} vs {t2})");
        Assert.True(t2 < t3, $"turn 2 must stay before turn 3 (was {t2} vs {t3})");
    }

    [Fact]
    public async Task StructCompact_FeedsEveryNonHeaderBlockToTheLlmOnce()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 4);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "STRUCT SUMMARY")]));

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        await StructStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        // The summarizer's prompt is the only place old turns survive struct compaction, so every
        // non-header block must be in it exactly once. Group 0 used to be appended by its own loop;
        // this pins the coverage that loop provided (and catches a re-added duplicate).
        var prompt = string.Join("\n", _chat.ReceivedMessages.SelectMany(m => m.Select(x => x.Text ?? "")));
        var fedBlocks = prompt.Split("[Block:").Length - 1;
        var expected = blocks.Count(b => b.Type != BlockType.agent_data);

        Assert.Equal(1, _chat.ResponseCallCount); // struct = one call for the whole history
        Assert.Equal(expected, fedBlocks);
    }

    [Fact]
    public async Task StructCompact_TwoTurns_DoesNothing()
    {
        const string agentId = "agent";
        await _db.Sessions.EnsureSessionAsync(agentId);
        await TestData.AppendHistoryAsync(_db.Blocks, agentId, turns: 2);
        _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "STRUCT SUMMARY")]));

        var blocks = await _db.Blocks.LoadBlocksAsync(agentId);
        var result = await StructStrategy.CompactAsync(
            agentId, DefaultOpts, blocks, "test-model", _db.Blocks, _chat, _db.Sessions, NullLogger.Instance, contextWindow: 10_000);

        // Both turns are safe zones: nothing to summarize, and nothing to pay the LLM for.
        Assert.False(result);
        Assert.Equal(0, _chat.ResponseCallCount);
        Assert.Equal(blocks.Count, (await _db.Blocks.LoadBlocksAsync(agentId)).Count);
    }

    // ── Missing header ──

    [Theory]
    [InlineData("fibo")]
    [InlineData("struct")]
    public async Task Compact_HistoryWithoutAgentData_ReturnsFalseWithoutCallingTheLlm(string strategy)
    {
        var agentId = $"agent-no-header-{strategy}";
        await _db.Sessions.EnsureSessionAsync(agentId);

        // Six completed turns and no agent_data: enough for both strategies to pick zones and
        // start summarizing, if they were allowed to get that far.
        var history = new List<MemoryBlock>();
        for (var t = 0; t < 6; t++)
        {
            history.Add(MemoryBlock.UserMessage($"u{t}"));
            history.Add(MemoryBlock.AgentMessage($"a{t}"));
            history.Add(MemoryBlock.TurnMarker());
        }
        await _db.Blocks.AppendBlocksAsync(agentId, history);

        for (var i = 0; i < 10; i++)
            _chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "SUMMARY")]));

        SetupCompression(DefaultOpts);
        var compacted = await _svc.CompactAsync(agentId, contextWindow: 10_000, strategy);

        Assert.False(compacted);
        // The header is the splice anchor: without it the rewrite has nowhere to go, so nothing may
        // be summarized. Paying for summaries and then discarding them is pure waste.
        Assert.Equal(0, _chat.ResponseCallCount);
        Assert.Equal(
            history.Select(b => b.Content),
            (await _db.Blocks.LoadBlocksAsync(agentId)).Select(b => b.Content));
    }

    /// <summary>Numbers are the ordering key for the whole transcript — duplicates break it.</summary>
    private static void AssertUniqueNumbers(List<MemoryBlock> blocks)
        => Assert.Equal(blocks.Count, blocks.Select(b => b.Number).Distinct().Count());
}
