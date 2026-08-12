using System.Text.Json;
using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

public class UsageParserTests
{
    private static (long Hit, long Miss, long Output) Parse(string json)
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
        => Assert.Equal((hit, miss, output), Parse(json));

    [Fact]
    public void DeepSeek_Format_Parsed()
        => Assert.Equal((80, 120, 50), Parse(
            """{"Usage":{"InputTokenCount":200,"OutputTokenCount":50,"InputTokenDetails":{"CachedTokenCount":80}}}"""));

    [Fact]
    public void Anthropic_Format_Parsed()
        => Assert.Equal((120, 180, 30), Parse(
            """{"usage":{"input_tokens":300,"output_tokens":30,"cache_read_input_tokens":120}}"""));

    [Fact]
    public void Google_Format_Parsed()
        => Assert.Equal((100, 300, 40), Parse(
            """{"usageMetadata":{"promptTokenCount":400,"candidatesTokenCount":40,"cachedContentTokenCount":100}}"""));

    // ── Unknown / malformed ──

    [Theory]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{}""")]
    [InlineData("""{"usage":"nope"}""")]
    [InlineData("""{"usage":{"prompt_tokens":0}}""")]
    public void UnknownOrMalformed_ReturnsZeroes(string json)
        => Assert.Equal((0, 0, 0), Parse(json));

    // ── Edge cases / hardening ──

    [Fact]
    public void CachedExceedsInput_ClampedToZeroMiss()
    {
        // Malformed provider data (cached > total) must not produce negative miss.
        Assert.Equal((150, 0, 10), Parse(
            """{"usage":{"prompt_tokens":100,"prompt_tokens_details":{"cached_tokens":150},"completion_tokens":10}}"""));
    }

    [Fact]
    public void NonNumericFields_Zeroed_ButFormatRecognized()
    {
        // completion_tokens exists (so the format is recognized) but is a string → output = 0.
        Assert.Equal((0, 100, 0), Parse(
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
