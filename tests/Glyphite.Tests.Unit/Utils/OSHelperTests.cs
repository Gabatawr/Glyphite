using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

/// <summary>
/// One rule for "where does a relative path point": the agent's directory. It lives in one place because
/// <c>write_file</c> and the re-read of the written file must land on the same file — resolving them
/// separately is how the model once got shown a same-named file from another directory.
/// </summary>
public class OSHelperTests
{
    [Fact]
    public void RelativePath_ResolvesAgainstTheBaseDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "glyphite-resolve", "project");

        var resolved = OSHelper.ResolvePath(Path.Combine("nested", "a.txt"), baseDir);

        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "nested", "a.txt")), resolved);
    }

    [Fact]
    public void RootedPath_IsReturnedUnchanged()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "abs.txt"));

        Assert.Equal(absolute, OSHelper.ResolvePath(absolute, Path.Combine(Path.GetTempPath(), "other")));
    }

    [Fact]
    public void NoBaseDirectory_FallsBackToTheProcessDirectory()
    {
        var resolved = OSHelper.ResolvePath("a.txt", null);

        Assert.Equal(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "a.txt")), resolved);
    }

    [Fact]
    public void ParentTraversal_IsResolved_NotClamped()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "glyphite-resolve", "project", "sub");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDir, "..", "sibling", "a.txt")),
            OSHelper.ResolvePath(Path.Combine("..", "sibling", "a.txt"), baseDir));
    }

    [Fact]
    public void EmptyPath_IsReturnedAsIs()
    {
        Assert.Equal("", OSHelper.ResolvePath("", "/base"));
    }
}
