using System.Text.Json;
using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

public class UsageParserTests
{
    private static (long Hit, long Miss, long Output, long Reasoning) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return UsageParser.Parse(doc);
    }

    // ── Format recognition ──

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":100,"prompt_tokens_details":{"cached_tokens":40},"completion_tokens":25}}""", 40, 60, 25)]
    [InlineData("""{"usage":{"prompt_tokens":100,"completion_tokens":10}}""", 0, 100, 10)]
    [InlineData("""{"usage":{"prompt_tokens":0,"completion_tokens":0}}""", 0, 0, 0)]
    public void OpenAi_Format_Parsed(string json, long hit, long miss, long output)
        => Assert.Equal((hit, miss, output, 0L), Parse(json));

    [Fact]
    public void DeepSeek_Format_Parsed()
        => Assert.Equal((80, 120, 50, 0), Parse(
            """{"Usage":{"InputTokenCount":200,"OutputTokenCount":50,"InputTokenDetails":{"CachedTokenCount":80}}}"""));

    [Fact]
    public void Anthropic_Format_Parsed()
        => Assert.Equal((120, 180, 30, 0), Parse(
            """{"usage":{"input_tokens":300,"output_tokens":30,"cache_read_input_tokens":120}}"""));

    [Fact]
    public void Google_Format_Parsed()
        => Assert.Equal((100, 300, 40, 0), Parse(
            """{"usageMetadata":{"promptTokenCount":400,"candidatesTokenCount":40,"cachedContentTokenCount":100}}"""));

    // ── Reasoning tokens (hidden thinking, billed as output) ──

    [Fact]
    public void OpenAi_ReasoningTokens_ReportedSeparately_AndIncludedInOutput()
    {
        // The real shape of this project's endpoint (captured from a live response):
        // completion_tokens already counts the thinking, reasoning_tokens only splits it out.
        var (hit, miss, output, reasoning) = Parse("""
            {"usage":{"prompt_tokens":43,"completion_tokens":22,"total_tokens":65,
                      "prompt_tokens_details":{"cached_tokens":0},
                      "completion_tokens_details":{"reasoning_tokens":20},
                      "prompt_cache_hit_tokens":0,"prompt_cache_miss_tokens":43}}
            """);

        Assert.Equal(43, miss);        // prompt is the only honest context measure
        Assert.Equal(22, output);      // billed completion, reasoning included
        Assert.Equal(20, reasoning);   // ...of which 20 are hidden thinking
        Assert.True(reasoning <= output);
    }

    [Fact]
    public void Reasoning_NotMoreThanOutput_IsClamped()
    {
        // A gateway that reports reasoning outside completion_tokens must not inflate the bill
        // beyond what was actually billed.
        var (_, _, output, reasoning) = Parse(
            """{"usage":{"prompt_tokens":10,"completion_tokens":5,"completion_tokens_details":{"reasoning_tokens":50}}}""");

        Assert.Equal(5, output);
        Assert.Equal(5, reasoning);
    }

    [Fact]
    public void Reasoning_MissingField_IsZero()
        => Assert.Equal(0, Parse("""{"usage":{"prompt_tokens":10,"completion_tokens":5}}""").Reasoning);

    [Fact]
    public void DeepSeek_ReasoningTokens_Parsed()
        => Assert.Equal((80, 120, 50, 30), Parse("""
            {"Usage":{"InputTokenCount":200,"OutputTokenCount":50,
                      "InputTokenDetails":{"CachedTokenCount":80},
                      "OutputTokenDetails":{"ReasoningTokenCount":30}}}
            """));

    [Fact]
    public void Google_Thoughts_AreAddedToOutput_NotDropped()
    {
        // Gemini reports thoughts *outside* candidatesTokenCount, so the billed output is their sum.
        var (hit, miss, output, reasoning) = Parse("""
            {"usageMetadata":{"promptTokenCount":400,"candidatesTokenCount":40,
                              "cachedContentTokenCount":100,"thoughtsTokenCount":15}}
            """);

        Assert.Equal(100, hit);
        Assert.Equal(300, miss);
        Assert.Equal(55, output);      // 40 visible + 15 thoughts
        Assert.Equal(15, reasoning);
    }

    [Fact]
    public void Anthropic_HasNoReasoningCounter_ButOutputStillBilled()
        => Assert.Equal((0, 300, 30, 0), Parse(
            """{"usage":{"input_tokens":300,"output_tokens":30}}"""));

    // ── Cache hit fallbacks ──

    [Fact]
    public void NonStandardPromptCacheHitTokens_UsedWhenDetailsMissing()
    {
        // Some OpenAI-compatible gateways only send prompt_cache_hit_tokens. Missing it would bill
        // every cached token at the (50x) miss price.
        Assert.Equal((70, 30, 10, 0), Parse(
            """{"usage":{"prompt_tokens":100,"completion_tokens":10,"prompt_cache_hit_tokens":70}}"""));
    }

    [Fact]
    public void StandardCachedTokens_WinsOverNonStandardField()
        => Assert.Equal((40, 60, 10, 0), Parse(
            """{"usage":{"prompt_tokens":100,"prompt_tokens_details":{"cached_tokens":40},"prompt_cache_hit_tokens":99,"completion_tokens":10}}"""));

    // ── Unknown / malformed ──

    [Theory]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{}""")]
    [InlineData("""{"usage":"nope"}""")]
    [InlineData("""{"usage":{"prompt_tokens":0}}""")]
    public void UnknownOrMalformed_ReturnsZeroes(string json)
        => Assert.Equal((0, 0, 0, 0), Parse(json));

    // ── Edge cases / hardening ──

    [Fact]
    public void CachedExceedsInput_ClampedToZeroMiss()
    {
        // Malformed provider data (cached > total) must not produce negative miss.
        Assert.Equal((150, 0, 10, 0), Parse(
            """{"usage":{"prompt_tokens":100,"prompt_tokens_details":{"cached_tokens":150},"completion_tokens":10}}"""));
    }

    [Fact]
    public void NonNumericFields_Zeroed_ButFormatRecognized()
    {
        // completion_tokens exists (so the format is recognized) but is a string → output = 0.
        Assert.Equal((0, 100, 0, 0), Parse(
            """{"usage":{"prompt_tokens":100,"completion_tokens":"ten"}}"""));
    }

    // ── Normalize ──

    [Fact]
    public void Normalize_Null_ReturnsNull()
        => Assert.Null(UsageParser.Normalize(null!));

    [Fact]
    public void Normalize_JsonElement_Parses()
    {
        using var doc = JsonDocument.Parse("""{"usage":{"prompt_tokens":5}}""");
        using var normalized = UsageParser.Normalize(doc.RootElement);
        Assert.NotNull(normalized);
        Assert.Equal(5, normalized!.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64());
    }

    [Fact]
    public void Normalize_SerializableObject_Parses()
    {
        using var normalized = UsageParser.Normalize(new { usage = new { prompt_tokens = 7 } });
        Assert.NotNull(normalized);
        Assert.Equal(7, normalized!.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64());
    }
}
