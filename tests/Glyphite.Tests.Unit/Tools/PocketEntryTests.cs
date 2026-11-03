using Glyphite.Host.Tools;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

/// <summary>Naming rules of a pocket entry: the alias, its native form, and the category.</summary>
public class PocketEntryTests
{
    [Theory]
    [InlineData("  MyTool  ", "mytool")]
    [InlineData("", "")]
    [InlineData("ALREADY-LOWER", "already-lower")]
    public void NormalizeName_TrimsAndLowercases(string input, string expected)
        => Assert.Equal(expected, PocketEntry.NormalizeName(input));

    [Fact]
    public void MaterializedName_AppendsSuffix()
        => Assert.Equal("foo_pocket", PocketEntry.MaterializedName("foo"));

    [Fact]
    public void MaterializedName_NoDoubleSuffix()
        => Assert.Equal("foo_pocket", PocketEntry.MaterializedName("foo_pocket"));

    [Fact]
    public void MaterializedName_TruncatesTo64Chars()
    {
        var result = PocketEntry.MaterializedName(new string('x', 62));
        Assert.Equal(64, result.Length);
        Assert.EndsWith("_pocket", result);
    }

    [Theory]
    [InlineData("", "common")]
    [InlineData("  XYZ  ", "xyz")]
    public void NormalizeCategory_DefaultsAndNormalizes(string input, string expected)
        => Assert.Equal(expected, PocketEntry.NormalizeCategory(input));

    [Theory]
    [InlineData("file", true)]
    [InlineData("a", true)]
    [InlineData("file_2", true)]
    [InlineData("File", false)]      // names are lowercased before validation, never accepted as-is
    [InlineData("my-tool", false)]   // a dash belongs to categories, not names
    [InlineData("", false)]
    public void IsValidName_AcceptsOnlyLowercaseUnderscore(string name, bool valid)
        => Assert.Equal(valid, PocketEntry.IsValidName(name));

    [Theory]
    [InlineData("manim", true)]
    [InlineData("research-tools", true)]
    [InlineData("common", true)]
    [InlineData("", false)]
    public void IsValidCategory_AcceptsLowercaseWithDash(string category, bool valid)
        => Assert.Equal(valid, PocketEntry.IsValidCategory(category));
}
