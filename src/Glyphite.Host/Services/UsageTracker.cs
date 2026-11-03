using System.Text.Json;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

/// <summary>Tracks LLM usage (cache hit/miss, output tokens) from streaming responses.
///
/// <see cref="TotalOutputTokens"/> is the billed completion count and already includes
/// <see cref="TotalReasoningTokens"/> (the hidden-thinking part). Reasoning is tracked separately
/// only to explain where the money goes: it is not a separate charge and must not be added to
/// output or treated as a measure of how much text landed in the history.
/// Prompt size (the only honest context measure) is <see cref="TotalCacheHitTokens"/> +
/// <see cref="TotalCacheMissTokens"/>.</summary>
public sealed class UsageTracker
{
    public long TotalCacheHitTokens { get; private set; }
    public long TotalCacheMissTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }
    public long TotalReasoningTokens { get; private set; }

    public long LastHitTokens { get; private set; }
    public long LastMissTokens { get; private set; }
    public long LastOutputTokens { get; private set; }
    public long LastReasoningTokens { get; private set; }

    /// <summary>Called after each iteration's usage is recorded: (hit, miss, output, reasoning).</summary>
    public event Action<long, long, long, long>? OnUsage;

    /// <summary>Extract and accumulate tokens from a completed iteration's updates.</summary>
    public void RecordUsage(List<ChatResponseUpdate> updates)
    {
        var (hit, miss, output, reasoning) = ExtractUsage(updates);
        if (hit > 0 || miss > 0 || output > 0)
        {
            TotalCacheHitTokens += hit;
            TotalCacheMissTokens += miss;
            TotalOutputTokens += output;
            TotalReasoningTokens += reasoning;
            LastHitTokens = hit;
            LastMissTokens = miss;
            LastOutputTokens = output;
            LastReasoningTokens = reasoning;
            OnUsage?.Invoke(hit, miss, output, reasoning);
        }
    }

    /// <summary>Extract (cacheHit, cacheMiss, output, reasoning) from a list of streaming updates.</summary>
    private static (long Hit, long Miss, long Output, long Reasoning) ExtractUsage(List<ChatResponseUpdate> updates)
    {
        var hit = 0L;
        var miss = 0L;
        var output = 0L;
        var reasoning = 0L;

        foreach (var update in updates)
        {
            if (update.RawRepresentation is null) continue;

            try
            {
                using var doc = UsageParser.Normalize(update.RawRepresentation);
                if (doc is null) continue;

                var (uHit, uMiss, uOutput, uReasoning) = UsageParser.Parse(doc);
                if (uHit + uMiss + uOutput == 0) continue;

                // Use Math.Max across updates — each streaming chunk may repeat totals
                if (uHit > 0) hit = Math.Max(hit, uHit);
                if (uMiss > 0) miss = Math.Max(miss, uMiss);
                if (uOutput > 0) output = Math.Max(output, uOutput);
                if (uReasoning > 0) reasoning = Math.Max(reasoning, uReasoning);
            }
            catch
            {
            }
        }

        return (hit, miss, output, reasoning);
    }
}
