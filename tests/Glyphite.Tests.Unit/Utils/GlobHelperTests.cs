using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

/// <summary>
/// Pattern matching behind <c>search_glob</c> and pocket-name masks. <c>*</c> and <c>?</c> are the
/// plain forms; <c>pathAware</c> adds <c>**</c> and stops a single <c>*</c> from crossing separators —
/// which is what makes <c>src/*.cs</c> mean one level and <c>src/**/*.cs</c> mean any depth.
/// </summary>
public class GlobHelperTests
{
    private static bool Matches(string pattern, string value, bool pathAware = false)
        => GlobHelper.ToRegex(pattern, pathAware).IsMatch(value);

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "Program.txt", false)]
    [InlineData("Pro?ram.cs", "Program.cs", true)]
    [InlineData("Pro?ram.cs", "Prgram.cs", false)]
    [InlineData("*.cs", "PROGRAM.CS", true)] // case-insensitive by design
    public void SimplePatterns(string pattern, string value, bool expected)
        => Assert.Equal(expected, Matches(pattern, value));

    [Fact]
    public void SingleStar_IsNotPathAware_SoItCrossesDirectories()
    {
        // Without pathAware, `*` becomes `.*` — the old behaviour had to be preserved for name masks.
        Assert.True(Matches("*.cs", "src/deep/Program.cs"));
    }

    [Theory]
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/deep/Program.cs", false)] // `*` stops at the separator
    [InlineData("src/**/*.cs", "src/deep/Program.cs", true)]
    [InlineData("src/**/*.cs", "src/a/b/Program.cs", true)]
    [InlineData("src/**", "src/deep/Program.cs", true)]
    [InlineData("src/**", "other/Program.cs", false)]
    [InlineData("**/*.cs", "Program.cs", true)] // `**/` may match zero segments
    [InlineData("**/*.cs", "src/Program.cs", true)]
    [InlineData("**", "anything/at/all", true)]
    public void PathAwarePatterns(string pattern, string value, bool expected)
        => Assert.Equal(expected, Matches(pattern, value, pathAware: true));

    [Fact]
    public void RegexCharactersInThePattern_AreLiteral()
    {
        Assert.True(Matches("a+b.cs", "a+b.cs"));
        Assert.False(Matches("a+b.cs", "aab.cs"));
        Assert.True(Matches("v1.2?.txt", "v1.23.txt"));
    }
}
