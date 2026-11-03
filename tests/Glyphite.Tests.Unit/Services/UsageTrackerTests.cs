using System.Text.Json;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

/// <summary>
/// Usage arrives in chunks and each chunk may repeat the totals, so the tracker takes the maximum
/// per field inside one iteration and sums across iterations. The two rulers must stay apart:
/// <c>Output</c> is what the provider billed (reasoning included), while only
/// <c>Hit + Miss</c> says how large the context was.
/// </summary>
public class UsageTrackerTests
{
    private static ChatResponseUpdate Update(string json) => new()
    {
        RawRepresentation = JsonDocument.Parse(json).RootElement,
    };

    private static string OpenAi(long prompt, long completion, long cached = 0, long reasoning = 0) =>
        $$"""
        {
          "usage": {
            "prompt_tokens": {{prompt}},
            "completion_tokens": {{completion}},
            "prompt_tokens_details": { "cached_tokens": {{cached}} },
            "completion_tokens_details": { "reasoning_tokens": {{reasoning}} }
          }
        }
        """;

    [Fact]
    public void RepeatedTotalsAcrossChunks_AreNotSummed()
    {
        var tracker = new UsageTracker();

        tracker.RecordUsage([
            Update(OpenAi(1_000, 50, cached: 900)),
            Update(OpenAi(1_000, 50, cached: 900)),
        ]);

        Assert.Equal(900, tracker.TotalCacheHitTokens);
        Assert.Equal(100, tracker.TotalCacheMissTokens);   // 1000 prompt - 900 cached
        Assert.Equal(50, tracker.TotalOutputTokens);
    }

    [Fact]
    public void EachIteration_Accumulates_AndLastKeepsTheFinalOne()
    {
        var tracker = new UsageTracker();

        tracker.RecordUsage([Update(OpenAi(1_000, 50))]);
        tracker.RecordUsage([Update(OpenAi(2_000, 70))]);

        Assert.Equal(3_000, tracker.TotalCacheMissTokens);
        Assert.Equal(120, tracker.TotalOutputTokens);
        Assert.Equal(2_000, tracker.LastMissTokens);
        Assert.Equal(70, tracker.LastOutputTokens);
    }

    [Fact]
    public void Reasoning_IsSeparate_ButAlreadyInsideOutput()
    {
        var tracker = new UsageTracker();

        tracker.RecordUsage([Update(OpenAi(1_000, 500, reasoning: 400))]);

        Assert.Equal(500, tracker.TotalOutputTokens);
        Assert.Equal(400, tracker.TotalReasoningTokens);
        // Not a second charge: reasoning is a detail of the completion count.
        Assert.True(tracker.TotalReasoningTokens <= tracker.TotalOutputTokens);
    }

    [Fact]
    public void OnUsage_ReportsPerIterationValues()
    {
        var tracker = new UsageTracker();
        var calls = new List<(long Hit, long Miss, long Output, long Reasoning)>();
        tracker.OnUsage += (h, m, o, r) => calls.Add((h, m, o, r));

        tracker.RecordUsage([Update(OpenAi(1_000, 50, cached: 900, reasoning: 10))]);

        var call = Assert.Single(calls);
        Assert.Equal((900L, 100L, 50L, 10L), call);
    }

    [Fact]
    public void UpdatesWithoutUsage_ChangeNothing()
    {
        var tracker = new UsageTracker();

        tracker.RecordUsage([new ChatResponseUpdate { RawRepresentation = new { choices = Array.Empty<object>() } }]);

        Assert.Equal(0, tracker.TotalCacheHitTokens + tracker.TotalCacheMissTokens + tracker.TotalOutputTokens);
        Assert.Equal(0, tracker.TotalReasoningTokens);
    }

    /// <summary>DeepSeek reports the same three numbers under a different shape.</summary>
    [Fact]
    public void DeepSeekFormat_IsUnderstood()
    {
        var tracker = new UsageTracker();

        tracker.RecordUsage([Update("""
            {"Usage":{"InputTokenCount":1000,"OutputTokenCount":40,
                      "InputTokenDetails":{"CachedTokenCount":800},
                      "OutputTokenDetails":{"ReasoningTokenCount":15}}}
            """)]);

        Assert.Equal(800, tracker.TotalCacheHitTokens);
        Assert.Equal(200, tracker.TotalCacheMissTokens);
        Assert.Equal(40, tracker.TotalOutputTokens);
        Assert.Equal(15, tracker.TotalReasoningTokens);
    }
}
