using System.Text.Json;

namespace Glyphite.Host.Utils;

/// <summary>Parses LLM usage from API response raw representations.
/// Supports DeepSeek, OpenAI, Anthropic, and Google Gemini formats.
///
/// The same response answers two unrelated questions, and the numbers must never be mixed up:
///
/// <list type="bullet">
/// <item><b>Billing</b> — <c>Hit + Miss</c> is the prompt, <c>Output</c> is the completion.
/// Providers bill reasoning tokens as output, so <c>Output</c> already contains them: this is
/// what cost ($) is computed from.</item>
/// <item><b>Context</b> — only <c>Hit + Miss</c> says how large the request was. <c>Output</c>
/// says nothing about how much text the answer occupies in the history: reasoning is partly
/// hidden from us and partly compressed away, so the stored blocks are always smaller than the
/// billed output (measured: 2.8x on a real base). Never use <c>Output</c> as a measure of
/// context growth.</item>
/// </list>
///
/// <c>Reasoning</c> is the hidden-thinking part already contained in <c>Output</c>. It is reported
/// for visibility (how much of the bill is thinking) and must not be added to the total again.</summary>
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

    /// <summary>Extract (cacheHit, cacheMiss, output, reasoning) tokens from a Usage JSON object.
    /// <c>output</c> is the billed total and already includes <c>reasoning</c>.
    /// Tries formats in order: OpenAI, DeepSeek, Anthropic, Google Gemini.</summary>
    public static (long Hit, long Miss, long Output, long Reasoning) Parse(JsonDocument doc)
    {
        // 1. Try OpenAI format: { usage: { prompt_tokens, completion_tokens,
        //       prompt_tokens_details: { cached_tokens },
        //       completion_tokens_details: { reasoning_tokens } } }
        if (TryParseOpenAI(doc, out var result))
            return result;

        // 2. Try DeepSeek format: { Usage: { InputTokenCount, OutputTokenCount,
        //       InputTokenDetails: { CachedTokenCount },
        //       OutputTokenDetails: { ReasoningTokenCount } } }
        if (TryParseDeepSeek(doc, out result))
            return result;

        // 3. Try Anthropic format: { usage: { input_tokens, output_tokens,
        //       cache_creation_input_tokens, cache_read_input_tokens } }
        if (TryParseAnthropic(doc, out result))
            return result;

        // 4. Try Google Gemini format: { usageMetadata: { promptTokenCount,
        //       candidatesTokenCount, cachedContentTokenCount, thoughtsTokenCount } }
        if (TryParseGoogle(doc, out result))
            return result;

        return (0, 0, 0, 0);
    }

    private static bool TryParseOpenAI(JsonDocument doc, out (long Hit, long Miss, long Output, long Reasoning) result)
    {
        result = (0, 0, 0, 0);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var inputTotal = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt64() : 0L;

        if (inputTotal == 0 && !usage.TryGetProperty("completion_tokens", out _))
            return false;

        // Standard path is usage.prompt_tokens_details.cached_tokens. Some OpenAI-compatible
        // gateways (this project's DeepSeek endpoint among them) also send the non-standard
        // prompt_cache_hit_tokens at the top level — and a missed hit is 50x the price of a
        // cached one, so falling back to it is worth the two lines.
        var cached = 0L;
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("cached_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                cached = ct.GetInt64();
        }
        if (cached == 0 && usage.TryGetProperty("prompt_cache_hit_tokens", out var pch) && pch.ValueKind == JsonValueKind.Number)
            cached = pch.GetInt64();

        var output = usage.TryGetProperty("completion_tokens", out var cpt) && cpt.ValueKind == JsonValueKind.Number
            ? cpt.GetInt64() : 0L;

        // Reasoning is a *detail* of completion_tokens — already inside `output`.
        var reasoning = 0L;
        if (usage.TryGetProperty("completion_tokens_details", out var outDetails) && outDetails.ValueKind == JsonValueKind.Object)
        {
            if (outDetails.TryGetProperty("reasoning_tokens", out var rt) && rt.ValueKind == JsonValueKind.Number)
                reasoning = rt.GetInt64();
        }

        var hit = cached > 0 ? cached : 0L;
        var miss = cached > 0 ? Math.Max(0, inputTotal - cached) : inputTotal;

        result = (hit, miss, output, Math.Clamp(reasoning, 0, output));
        return true;
    }

    private static bool TryParseDeepSeek(JsonDocument doc, out (long Hit, long Miss, long Output, long Reasoning) result)
    {
        result = (0, 0, 0, 0);
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

        var output = usage.TryGetProperty("OutputTokenCount", out var otc) && otc.ValueKind == JsonValueKind.Number
            ? otc.GetInt64() : 0L;

        var reasoning = 0L;
        if (usage.TryGetProperty("OutputTokenDetails", out var outDetails) && outDetails.ValueKind == JsonValueKind.Object)
        {
            if (outDetails.TryGetProperty("ReasoningTokenCount", out var rtc) && rtc.ValueKind == JsonValueKind.Number)
                reasoning = rtc.GetInt64();
        }

        var hit = cached > 0 ? cached : 0L;
        var miss = cached > 0 ? Math.Max(0, inputTotal - cached) : inputTotal;

        result = (hit, miss, output, Math.Clamp(reasoning, 0, output));
        return true;
    }

    private static bool TryParseAnthropic(JsonDocument doc, out (long Hit, long Miss, long Output, long Reasoning) result)
    {
        result = (0, 0, 0, 0);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var inputTotal = usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number
            ? it.GetInt64() : 0L;

        if (inputTotal == 0 && !usage.TryGetProperty("output_tokens", out _))
            return false;

        var cacheRead = 0L;
        if (usage.TryGetProperty("cache_read_input_tokens", out var crt) && crt.ValueKind == JsonValueKind.Number)
            cacheRead = crt.GetInt64();

        var output = usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number
            ? ot.GetInt64() : 0L;

        // Anthropic bills thinking tokens as output and reports no separate counter.
        var hit = cacheRead > 0 ? cacheRead : 0L;
        var miss = cacheRead > 0 ? Math.Max(0, inputTotal - cacheRead) : inputTotal;

        result = (hit, miss, output, 0);
        return true;
    }

    /// <summary>
    /// Google Gemini native format:
    /// {
    ///   "usageMetadata": {
    ///     "promptTokenCount": &lt;total_input&gt;,
    ///     "candidatesTokenCount": &lt;visible_output&gt;,
    ///     "cachedContentTokenCount": &lt;cached_input&gt;,
    ///     "thoughtsTokenCount": &lt;thinking_output&gt;
    ///   }
    /// }
    ///
    /// Unlike OpenAI, Gemini reports thoughts *outside* candidatesTokenCount, so the billed output
    /// is their sum — keeping only candidatesTokenCount would undercount every reasoning call.
    ///
    /// Note: Google also offers an OpenAI-compatible endpoint at
    /// https://generativelanguage.googleapis.com/v1beta/openai/ which
    /// returns standard OpenAI format (handled by TryParseOpenAI).
    /// </summary>
    private static bool TryParseGoogle(JsonDocument doc, out (long Hit, long Miss, long Output, long Reasoning) result)
    {
        result = (0, 0, 0, 0);
        if (!doc.RootElement.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var inputTotal = usage.TryGetProperty("promptTokenCount", out var ptc) && ptc.ValueKind == JsonValueKind.Number
            ? ptc.GetInt64() : 0L;

        if (inputTotal == 0 && !usage.TryGetProperty("candidatesTokenCount", out _))
            return false;

        var cached = 0L;
        if (usage.TryGetProperty("cachedContentTokenCount", out var cct) && cct.ValueKind == JsonValueKind.Number)
            cached = cct.GetInt64();

        var candidates = usage.TryGetProperty("candidatesTokenCount", out var ctc) && ctc.ValueKind == JsonValueKind.Number
            ? ctc.GetInt64() : 0L;

        var thoughts = usage.TryGetProperty("thoughtsTokenCount", out var ttc) && ttc.ValueKind == JsonValueKind.Number
            ? ttc.GetInt64() : 0L;

        var hit = cached > 0 ? cached : 0L;
        var miss = cached > 0 ? Math.Max(0, inputTotal - cached) : inputTotal;

        result = (hit, miss, candidates + thoughts, thoughts);
        return true;
    }
}
