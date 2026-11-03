using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

/// <summary>
/// Containment rules for working directories. The bug these pin: a rule of "/" was trimmed to "",
/// and every absolute path starts with "/", so the shipped default list ("/", "/etc", "/boot", ...)
/// rejected every absolute working directory — including the project the agent was asked to work in.
/// </summary>
public class PathGuardTests
{
    private static readonly string Base = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "glyphite-guard", "proj"));

    // ── A root rule means the root, not the whole filesystem ──

    [Fact]
    public void RootRule_BlocksTheRootItself() =>
        Assert.Equal("matches blocked path '/'", PathGuard.BlockReason("/", ["/"], Base));

    [Theory]
    [InlineData("/home/gab/project")]
    [InlineData("/tmp")]
    [InlineData("/var/www/app")]
    [InlineData("/etc")]
    public void RootRule_DoesNotBlockEveryAbsolutePath(string workdir) =>
        Assert.Null(PathGuard.BlockReason(workdir, ["/"], Base));

    // ── Rules match on a path-segment boundary ──

    [Theory]
    [InlineData("/etc")]
    [InlineData("/etc/")]
    [InlineData("/etc/passwd")]
    [InlineData("/etc/../etc/passwd")]
    [InlineData("/etc/./passwd")]
    public void Rule_CoversItselfAndEverythingBelow(string workdir) =>
        Assert.NotNull(PathGuard.BlockReason(workdir, ["/etc"], Base));

    [Theory]
    [InlineData("/etcetera")]
    [InlineData("/etc-old")]
    [InlineData("/var/etc")]
    public void Rule_DoesNotMatchAStringPrefix(string workdir) =>
        Assert.Null(PathGuard.BlockReason(workdir, ["/etc"], Base));

    [Fact]
    public void Rule_IgnoresATrailingSeparator() =>
        Assert.NotNull(PathGuard.BlockReason("/etc/passwd", ["/etc/"], Base));

    [Fact]
    public void EmptyRules_AreSkipped() =>
        Assert.Null(PathGuard.BlockReason("/home/x", ["", "   "], Base));

    // ── Relative workdirs resolve from the shell's start directory ──

    [Theory]
    [InlineData("/home/gab/project")]
    [InlineData("/tmp")]
    [InlineData("sub")]
    [InlineData("./sub")]
    [InlineData("../sibling")]
    public void AllowedDirectory_ReturnsNoReason(string workdir) =>
        Assert.Null(PathGuard.BlockReason(workdir, ["/etc", "/proc"], Base));

    [Fact]
    public void RelativeWorkdir_IsResolvedFromTheBaseDirectory()
    {
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "glyphite-guard", "outside"));
        var elsewhere = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "glyphite-guard", "elsewhere"));
        var rule = Path.Combine(elsewhere, "sub");

        // "sub" lands inside the rule only when the shell starts in `elsewhere`.
        Assert.Equal($"matches blocked path '{rule}'", PathGuard.BlockReason("sub", [rule], elsewhere));
        Assert.Null(PathGuard.BlockReason("sub", [rule], outside));
    }

    [Fact]
    public void ParentTraversal_IsCanonicalizedBeforeComparing()
    {
        var project = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "glyphite-guard", "project"));
        var nested = Path.Combine(project, "deep");

        // ".." stays inside the project → blocked; "../../sibling" leaves it → allowed.
        Assert.NotNull(PathGuard.BlockReason("..", [project], nested));
        Assert.Null(PathGuard.BlockReason("../../sibling", [project], nested));
    }

    // ── Nothing resolvable means nothing to compare ──

    [Fact]
    public void UnresolvableDirectory_IsRefusedInsteadOfGuessed() =>
        Assert.Equal("cannot be resolved to a directory", PathGuard.BlockReason("bad\0path", ["/etc"], Base));

    [Fact]
    public void BrokenBaseDirectory_FallsBackToTheProcessDirectory()
    {
        // A base that cannot be resolved must not turn into "no rules apply": the path is still judged,
        // just from the process directory.
        var reason = PathGuard.BlockReason("/etc/passwd", ["/etc"], "bad\0base");

        Assert.NotNull(reason);
    }
}
