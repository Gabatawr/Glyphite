using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class ContentDedupTests
{
    private static ContentDedupOptions Opts(
        int minLines = 3, double threshold = 0.2, int minLineLength = 4, int maxAliases = 5)
        => new() { MinLines = minLines, FrequencyThreshold = threshold, MinLineLength = minLineLength, MaxAliases = maxAliases };

    // ── No-op paths ──

    [Fact]
    public void EmptyOrNull_ReturnsUnchanged()
    {
        Assert.Equal("", ContentDedup.Compress("", Opts()));
        Assert.Null(ContentDedup.Compress(null!, Opts()));
    }

    [Fact]
    public void BelowMinLines_ReturnsUnchanged()
    {
        var input = "alpha\nbeta\ngamma";
        Assert.Equal(input, ContentDedup.Compress(input, Opts(minLines: 5)));
    }

    [Fact]
    public void NoFrequentLines_ReturnsUnchanged()
    {
        // "aaaa" repeats but stays below the 90% frequency threshold.
        var input = "aaaa\naaaa\nbbbb";
        Assert.Equal(input, ContentDedup.Compress(input, Opts(threshold: 0.9)));
    }

    [Fact]
    public void ShortLines_NeverAliased()
    {
        // "ab" is below MinLineLength, so no candidates → unchanged.
        var input = "ab\nab\ncd";
        Assert.Equal(input, ContentDedup.Compress(input, Opts()));
    }

    // ── Aliasing ──

    [Fact]
    public void RepeatedLines_AliasedWithTrSequence()
    {
        var result = ContentDedup.Compress("alpha\nbeta\nbeta\nbeta\ngamma", Opts());

        Assert.Equal("[ALIASES]\n  {A1} = beta\n[/ALIASES]\nalpha\n{A1}\n{TR:2}\ngamma", result);
    }

    [Fact]
    public void MaxAliases_CapsNumberOfAliases()
    {
        var result = ContentDedup.Compress("aaaa\naaaa\naaaa\nbbbb\nbbbb\ncccc", Opts(maxAliases: 1));

        Assert.Equal("[ALIASES]\n  {A1} = aaaa\n[/ALIASES]\n{A1}\n{TR:2}\nbbbb\nbbbb\ncccc", result);
    }

    [Fact]
    public void LiteralAliasTokens_NotConfusedWithReferences()
    {
        // The literal "{A1}" line must be escaped internally so it is not treated
        // as a reference to the alias of "abcd".
        var result = ContentDedup.Compress("abcd\nabcd\n{A1}", Opts());

        Assert.Equal("[ALIASES]\n  {A1} = abcd\n[/ALIASES]\n{A1}\n{TR:1}\n{A1}", result);
    }

    // ── Normalization ──

    [Fact]
    public void Crlf_IsNormalizedBeforeAliasing()
    {
        var result = ContentDedup.Compress("aaaa\r\naaaa\r\nbbbb", Opts());

        Assert.Equal("[ALIASES]\n  {A1} = aaaa\n[/ALIASES]\n{A1}\n{TR:1}\nbbbb", result);
    }

    [Fact]
    public void ControlChars_EscapedInLegend()
    {
        var result = ContentDedup.Compress("ab\u0001c\nab\u0001c\nbbbb", Opts());

        Assert.Equal("[ALIASES]\n  {A1} = ab\\u0001c\n[/ALIASES]\n{A1}\n{TR:1}\nbbbb", result);
    }
}
