using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

/// <summary>
/// Values are interpolated into shell command templates (pocket tools, <c>workdir</c>), so quoting is
/// the only thing standing between a path and shell interpretation.
/// </summary>
public class ShellHelperTests
{
    [Fact]
    public void QuoteSingle_WrapsInSingleQuotes()
        => Assert.Equal("'ls -la'", ShellHelper.QuoteSingle("ls -la"));

    /// <summary>A single quote cannot be escaped inside single quotes: bash closes, escapes, reopens.</summary>
    [Fact]
    public void QuoteSingle_EscapesEmbeddedQuotes()
        => Assert.Equal("'it'\\''s'", ShellHelper.QuoteSingle("it's"));

    [Fact]
    public void QuoteSingle_Empty_IsAnEmptyQuotedString()
        => Assert.Equal("''", ShellHelper.QuoteSingle(""));

    /// <summary>Everything inside single quotes is literal — this is the point of quoting at all.</summary>
    [Theory]
    [InlineData("$(rm -rf ~)")]
    [InlineData("`rm -rf ~`")]
    [InlineData("a; rm -rf ~")]
    [InlineData("a && rm -rf ~")]
    [InlineData("$HOME")]
    public void QuoteSingle_NeutralisesSubstitutionAndSeparators(string value)
    {
        // No escaping is added for these characters: they are inert because they sit inside quotes.
        Assert.Equal($"'{value}'", ShellHelper.QuoteSingle(value));
    }
}
