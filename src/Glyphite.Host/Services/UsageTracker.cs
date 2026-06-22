using System.Text.Json;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

/// <summary>Tracks LLM usage (cache hit/miss, output tokens) from streaming responses.</summary>
public sealed class UsageTracker
{
    public long TotalCacheHitTokens { get; private set; }
    public long TotalCacheMissTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }

    public long LastHitTokens { get; private set; }
    public long LastMissTokens { get; private set; }

    /// <summary>Called after each iteration's usage is recorded.</summary>
    public event Action<long, long, long>? OnUsage;

    /// <summary>Extract and accumulate tokens from a completed iteration's updates.</summary>
    public void RecordUsage(List<ChatResponseUpdate> updates)
    {
        var (hit, miss, output) = ExtractCacheTokens(updates);
        if (hit > 0 || miss > 0 || output > 0)
        {
            TotalCacheHitTokens += hit;
            TotalCacheMissTokens += miss;
            TotalOutputTokens += output;
            LastHitTokens = hit;
            LastMissTokens = miss;
            OnUsage?.Invoke(hit, miss, output);
        }
    }

    /// <summary>Extract (cacheHit, cacheMiss, output) from a list of streaming updates.</summary>
    private static (long Hit, long Miss, long Output) ExtractCacheTokens(List<ChatResponseUpdate> updates)
    {
        var hit = 0L;
        var miss = 0L;
        var output = 0L;

        foreach (var update in updates)
        {
            if (update.RawRepresentation is null) continue;

            try
            {
                using var doc = UsageParser.Normalize(update.RawRepresentation);
                if (doc is null) continue;

                var (uHit, uMiss, uOutput) = UsageParser.Parse(doc);
                if (uHit + uMiss + uOutput == 0) continue;

                // Use Math.Max across updates — each streaming chunk may repeat totals
                if (uHit > 0) hit = Math.Max(hit, uHit);
                if (uMiss > 0) miss = Math.Max(miss, uMiss);
                if (uOutput > 0) output = Math.Max(output, uOutput);
            }
            catch
            {
            }
        }

        return (hit, miss, output);
    }
}
