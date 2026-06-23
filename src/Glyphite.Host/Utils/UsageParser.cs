using System.Text.Json;

namespace Glyphite.Host.Utils;

/// <summary>Parses LLM usage (cache hit/miss, output tokens) from API response raw representations.
/// Supports DeepSeek, OpenAI, Anthropic, and Google Gemini formats.</summary>
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
    /// Tries formats in order: OpenAI, DeepSeek, Anthropic, Google Gemini.</summary>
    public static (long Hit, long Miss, long Output) Parse(JsonDocument doc)
    {
        // 1. Try OpenAI format: { usage: { prompt_tokens, completion_tokens,
        //       prompt_tokens_details: { cached_tokens } } }
        if (TryParseOpenAI(doc, out var result))
            return result;

        // 2. Try DeepSeek format: { Usage: { InputTokenCount, OutputTokenCount,
        //       InputTokenDetails: { CachedTokenCount } } }
        if (TryParseDeepSeek(doc, out result))
            return result;

        // 3. Try Anthropic format: { usage: { input_tokens, output_tokens,
        //       cache_creation_input_tokens, cache_read_input_tokens } }
        if (TryParseAnthropic(doc, out result))
            return result;

        // 4. Try Google Gemini format: { usageMetadata: { promptTokenCount,
        //       candidatesTokenCount, cachedContentTokenCount } }
        if (TryParseGoogle(doc, out result))
            return result;

        return (0, 0, 0);
    }

    private static bool TryParseOpenAI(JsonDocument doc, out (long Hit, long Miss, long Output) result)
    {
        result = (0, 0, 0);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var inputTotal = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt64() : 0L;

        if (inputTotal == 0 && !usage.TryGetProperty("completion_tokens", out _))
            return false;

        // prompt_cache_hit_tokens is NOT a standard OpenAI field.
        // The correct path is usage.prompt_tokens_details.cached_tokens.
        var cached = 0L;
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("cached_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                cached = ct.GetInt64();
        }

        var hit = cached > 0 ? cached : 0L;
        var miss = cached > 0 ? inputTotal - cached : inputTotal;
        var output = usage.TryGetProperty("completion_tokens", out var cpt) && cpt.ValueKind == JsonValueKind.Number
            ? cpt.GetInt64() : 0L;

        result = (hit, miss, output);
        return true;
    }

    private static bool TryParseDeepSeek(JsonDocument doc, out (long Hit, long Miss, long Output) result)
    {
        result = (0, 0, 0);
        if (!doc.RootElement.TryGetProperty("Usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var inputTotal = usage.TryGetProperty("InputTokenCount", out var itc) && itc.ValueKind == JsonValueKind.Number
            ? itc.GetInt64() : 0L;

        if (inputTotal == 0 && !usage.TryGetProperty("OutputTokenCount", out _))
            return false;

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

        result = (hit, miss, output);
        return true;
    }

    private static bool TryParseAnthropic(JsonDocument doc, out (long Hit, long Miss, long Output) result)
    {
        result = (0, 0, 0);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var inputTotal = usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number
            ? it.GetInt64() : 0L;

        if (inputTotal == 0 && !usage.TryGetProperty("output_tokens", out _))
            return false;

        var cacheRead = 0L;
        if (usage.TryGetProperty("cache_read_input_tokens", out var crt) && crt.ValueKind == JsonValueKind.Number)
            cacheRead = crt.GetInt64();

        var hit = cacheRead > 0 ? cacheRead : 0L;
        var miss = cacheRead > 0 ? inputTotal - cacheRead : inputTotal;
        var output = usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number
            ? ot.GetInt64() : 0L;

        result = (hit, miss, output);
        return true;
    }

    /// <summary>
    /// Google Gemini native format:
    /// {
    ///   "usageMetadata": {
    ///     "promptTokenCount": &lt;total_input&gt;,
    ///     "candidatesTokenCount": &lt;total_output&gt;,
    ///     "cachedContentTokenCount": &lt;cached_input&gt;
    ///   }
    /// }
    ///
    /// Note: Google also offers an OpenAI-compatible endpoint at
    /// https://generativelanguage.googleapis.com/v1beta/openai/ which
    /// returns standard OpenAI format (handled by TryParseOpenAI).
    /// </summary>
    private static bool TryParseGoogle(JsonDocument doc, out (long Hit, long Miss, long Output) result)
    {
        result = (0, 0, 0);
        if (!doc.RootElement.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var inputTotal = usage.TryGetProperty("promptTokenCount", out var ptc) && ptc.ValueKind == JsonValueKind.Number
            ? ptc.GetInt64() : 0L;

        if (inputTotal == 0 && !usage.TryGetProperty("candidatesTokenCount", out _))
            return false;

        var cached = 0L;
        if (usage.TryGetProperty("cachedContentTokenCount", out var cct) && cct.ValueKind == JsonValueKind.Number)
            cached = cct.GetInt64();

        var hit = cached > 0 ? cached : 0L;
        var miss = cached > 0 ? inputTotal - cached : inputTotal;
        var output = usage.TryGetProperty("candidatesTokenCount", out var ctc) && ctc.ValueKind == JsonValueKind.Number
            ? ctc.GetInt64() : 0L;

        result = (hit, miss, output);
        return true;
    }
}
