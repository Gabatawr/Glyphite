using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

/// <summary>
/// Invariants that must hold for <em>any</em> history, for both compaction strategies.
///
/// The expensive part of compaction is the LLM call that replaces old turns with a summary, and
/// the strategy then hard-deletes the rewritten range (<c>ReplaceBlocksSinceAsync</c>). So the
/// one thing that must never happen is a turn disappearing without a summary covering it.
///
/// Histories are generated from fixed seeds: deterministic, but varied in shape — with and without
/// agent_data, 1..6 turns, turns without assistant text, and a final turn that never completed
/// (no turn marker), which is what a crash or an Escape leaves behind.
/// </summary>
public class CompactionInvariantsTests : IDisposable
{
    private static readonly CompressionOptions Opts = new() { AutoCompress = true, AutoThreshold = 50 };

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    public static TheoryData<int, string> Cases()
    {
        var data = new TheoryData<int, string>();
        for (var seed = 0; seed < 12; seed++)
        {
            data.Add(seed, "fibo");
            data.Add(seed, "struct");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Compaction_NeverLosesHistorySilently(int seed, string strategy)
    {
        var agentId = $"agent-{strategy}-{seed}";
        await _db.Sessions.EnsureSessionAsync(agentId);

        var history = BuildHistory(seed, out var hasAgentData, out var userTexts);
        await _db.Blocks.AppendBlocksAsync(agentId, history);

        // Enough scripted summaries for every zone the strategy may produce.
        var chat = new FakeChatClient();
        for (var i = 0; i < 50; i++)
            chat.QueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "SUMMARY")]));

        await new CompactionService(_db.Blocks, _db.Sessions, Cfg(), chat, NullLogger<CompactionService>.Instance)
            .CompactAsync(agentId, contextWindow: 10_000, strategy);

        var after = await _db.Blocks.LoadBlocksAsync(agentId);

        // 1. Numbers are the transcript's ordering key — never duplicated.
        Assert.Equal(after.Count, after.Select(b => b.Number).Distinct().Count());

        // 2. The session header survives exactly once and stays first.
        if (hasAgentData)
        {
            Assert.Single(after, b => b.Type == BlockType.agent_data);
            Assert.Equal(BlockType.agent_data, after[0].Type);
        }

        // 3. Every user message is either still present or was fed to a summarizer.
        var summarizedText = string.Concat(chat.ReceivedMessages.SelectMany(m => m.Select(x => x.Text ?? "")));
        foreach (var text in userTexts)
        {
            var present = after.Any(b => b.Content == text);
            Assert.True(present || summarizedText.Contains(text),
                $"user message '{text}' vanished without being summarized (strategy={strategy}, seed={seed})");
        }
    }

    /// <summary>Build a pseudo-random history in the shape the application writes.
    /// Numbers are left to the block store, as in production.</summary>
    private static List<MemoryBlock> BuildHistory(int seed, out bool hasAgentData, out List<string> userTexts)
    {
        var rnd = new Random(seed);
        hasAgentData = rnd.Next(4) != 0;      // 25% of cases: no agent_data block at all
        var turns = rnd.Next(1, 7);
        var blocks = new List<MemoryBlock>();
        userTexts = [];

        if (hasAgentData)
            blocks.Add(MemoryBlock.AgentData("agent", "probe"));

        for (var t = 0; t < turns; t++)
        {
            var question = $"u{seed}_{t}";
            userTexts.Add(question);
            blocks.Add(MemoryBlock.UserMessage(question));

            var answers = rnd.Next(0, 3);
            for (var a = 0; a < answers; a++)
                blocks.Add(MemoryBlock.AgentMessage($"a{seed}_{t}_{a}"));

            // The last turn may be unfinished (crash / Escape) → no turn marker.
            if (t < turns - 1 || rnd.Next(2) == 0)
                blocks.Add(MemoryBlock.TurnMarker());
        }

        return blocks;
    }

    private static IConfigService Cfg()
    {
        var cfg = Substitute.For<IConfigService>();
        cfg.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, Arg.Any<string?>()).Returns(Opts);
        return cfg;
    }
}
