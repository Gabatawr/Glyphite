using System.Text.Json;

namespace Glyphite.Host.Utils;

/// <summary>Parses LLM usage (cache hit/miss, output tokens) from API response raw representations.</summary>
internal static class UsageParser
{
    /// <summary>Normalize a RawRepresentation object into a JsonDocument for structured access.</summary>
    public static JsonDocument? Normalize(object raw)
    {
        return raw switch
        {
            JsonDocument jd => JsonDocument.Parse(jd.RootElement.GetRawText()),
            JsonElement je => JsonDocument.Parse(je.GetRawText()),
            _ => raw is not null ? JsonDocument.Parse(JsonSerializer.Serialize(raw)) : null
        };
    }

    /// <summary>Extract (cacheHit, cacheMiss, output) tokens from a Usage JSON object.
    /// Expects the root element to have a "Usage" child with InputTokenCount, OutputTokenCount,
    /// and optionally InputTokenDetails.CachedTokenCount.</summary>
    public static (long Hit, long Miss, long Output) Parse(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("Usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0, 0);

        var inputTotal = usage.TryGetProperty("InputTokenCount", out var itc) && itc.ValueKind == JsonValueKind.Number
            ? itc.GetInt64() : 0L;
        var cached = 0L;
        if (usage.TryGetProperty("InputTokenDetails", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("CachedTokenCount", out var ctc) && ctc.ValueKind == JsonValueKind.Number)
                cached = ctc.GetInt64();
        }

        var hit = cached > 0 ? cached : 0L;
        var miss = cached > 0 ? inputTotal - cached : inputTotal;
        var output = usage.TryGetProperty("OutputTokenCount", out var otc) && otc.ValueKind == JsonValueKind.Number
            ? otc.GetInt64() : 0L;

        return (hit, miss, output);
    }
}
