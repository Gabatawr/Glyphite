using System.Text.Json;
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
                using var doc = GetUsageDocument(update.RawRepresentation);
                if (doc is null) continue;
                if (!doc.RootElement.TryGetProperty("Usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                    continue;

                var inputTotal = usage.TryGetProperty("InputTokenCount", out var itc) && itc.ValueKind == JsonValueKind.Number
                    ? itc.GetInt64() : 0L;
                var cached = 0L;
                if (usage.TryGetProperty("InputTokenDetails", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    if (details.TryGetProperty("CachedTokenCount", out var ctc) && ctc.ValueKind == JsonValueKind.Number)
                        cached = ctc.GetInt64();
                }

                if (cached > 0)
                {
                    hit = Math.Max(hit, cached);
                    miss = Math.Max(miss, inputTotal - cached);
                }
                else if (inputTotal > 0)
                {
                    miss = Math.Max(miss, inputTotal);
                }

                if (usage.TryGetProperty("OutputTokenCount", out var otc) && otc.ValueKind == JsonValueKind.Number)
                    output = Math.Max(output, otc.GetInt64());
            }
            catch
            {
            }
        }

        return (hit, miss, output);
    }

    /// <summary>Parse RawRepresentation into a JsonDocument without double-serialization.</summary>
    private static JsonDocument? GetUsageDocument(object raw)
    {
        return raw switch
        {
            JsonDocument jd => JsonDocument.Parse(jd.RootElement.GetRawText()),
            JsonElement je => JsonDocument.Parse(je.GetRawText()),
            _ => JsonDocument.Parse(JsonSerializer.Serialize(raw))
        };
    }
}
