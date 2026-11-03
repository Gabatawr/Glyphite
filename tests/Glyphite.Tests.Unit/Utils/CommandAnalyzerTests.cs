using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

/// <summary>
/// The bypass table. Every "true" row is a command that used to slip past a checker that only looked
/// at the first word of the whole string; every "false" row is a command that merely *mentions* the
/// pattern and must not be hard-blocked.
/// </summary>
public class CommandAnalyzerTests
{
    [Theory]
    // separators
    [InlineData("cd /tmp && rm -rf ~", "rm -rf")]
    [InlineData("echo hi; rm -rf ~", "rm -rf")]
    [InlineData("echo hi || rm -rf ~", "rm -rf")]
    [InlineData("true | rm -rf ~", "rm -rf")]
    [InlineData("rm -rf ~ &", "rm -rf")]
    [InlineData("echo one\nrm -rf ~", "rm -rf")]
    [InlineData("(cd /tmp; rm -rf x)", "rm -rf")]
    // environment assignment prefix
    [InlineData("FOO=1 rm -rf ~", "rm -rf")]
    [InlineData("FOO=1 BAR=2 rm -rf ~", "rm -rf")]
    // wrappers
    [InlineData("sudo reboot", "reboot")]
    [InlineData("cd /tmp && sudo reboot", "reboot")]
    [InlineData("/usr/bin/sudo reboot", "reboot")]
    [InlineData("sudo -u root rm -rf /", "rm -rf")]
    [InlineData("sudo --user=root rm -rf /", "rm -rf")]
    [InlineData("sudo -E rm -rf /", "rm -rf")]
    [InlineData("env -i rm -rf /", "rm -rf")]
    [InlineData("env FOO=bar rm -rf /", "rm -rf")]
    [InlineData("nohup rm -rf / &", "rm -rf")]
    [InlineData("timeout 30 rm -rf /", "rm -rf")]
    [InlineData("timeout --signal=KILL 30 rm -rf /", "rm -rf")]
    [InlineData("xargs rm -rf", "rm -rf")]
    // substitutions
    [InlineData("echo $(rm -rf /)", "rm -rf")]
    [InlineData("echo `rm -rf /`", "rm -rf")]
    // shells with -c
    [InlineData("bash -c \"rm -rf /\"", "rm -rf")]
    [InlineData("sh -lc 'rm -rf /'", "rm -rf")]
    [InlineData("sudo bash -c \"rm -rf /\"", "rm -rf")]
    [InlineData("env -i bash -c \"rm -rf /\"", "rm -rf")]
    // multiple words and whitespace
    [InlineData("git push --force origin main", "git push --force")]
    [InlineData("sudo git push --force", "git push --force")]
    [InlineData("rm    -rf     ~", "rm -rf")]
    [InlineData("RM -RF /", "rm -rf")]
    [InlineData("\\rm -rf /", "rm -rf")]
    public void FindsThePatternWhereItActuallyRuns(string command, string pattern)
    {
        Assert.Equal(pattern, CommandAnalyzer.FindMatch(command, [pattern]));
    }

    [Theory]
    [InlineData("echo \"rm -rf\"", "rm -rf")]              // printed, not run
    [InlineData("git log --grep \"rm -rf\"", "rm -rf")]
    [InlineData("grep -r \"sudo reboot\" .", "reboot")]
    [InlineData("rm -rfx /tmp/thing", "rm -rf")]           // different command name
    [InlineData("git push origin main", "git push --force")]
    [InlineData("cat notes.txt | grep rm", "rm -rf")]
    [InlineData("echo \"bash -c rm -rf\"", "rm -rf")]
    public void DoesNotMatchAPatternThatIsOnlyAnArgument(string command, string pattern)
    {
        Assert.Null(CommandAnalyzer.FindMatch(command, [pattern]));
    }

    [Fact]
    public void ListsEverySegmentWithWrappersPeeled()
    {
        var segments = CommandAnalyzer.Segments("cd /tmp && sudo -u root rm -rf build; echo done");

        Assert.Equal(["cd /tmp", "rm -rf build", "echo done"], segments);
    }

    [Fact]
    public void FindsTheFirstMatchingPatternAmongMany()
    {
        var match = CommandAnalyzer.FindMatch("ls -la && git push --force", ["shutdown", "git push --force"]);

        Assert.Equal("git push --force", match);
    }

    [Fact]
    public void RedirectIsNotASeparator()
    {
        // `2>&1` must not split into a bogus `1` command — the pipe is a real separator, the
        // ampersand inside the redirection is not.
        Assert.Equal(["make 2>&1", "tee build.log"], CommandAnalyzer.Segments("make 2>&1 | tee build.log"));
    }

    [Fact]
    public void EmptyInputAndEmptyPatternsAreHarmless()
    {
        Assert.Empty(CommandAnalyzer.Segments(""));
        Assert.Null(CommandAnalyzer.FindMatch("", ["rm -rf"]));
        Assert.Null(CommandAnalyzer.FindMatch("rm -rf /", []));
        Assert.Null(CommandAnalyzer.FindMatch("rm -rf /", null));
        Assert.Null(CommandAnalyzer.FindMatch("rm -rf /", [""]));
    }

    [Fact]
    public void PathologicalNestingTerminates()
    {
        var command = string.Concat(Enumerable.Repeat("$(", 40)) + "rm -rf /" + new string(')', 40);

        // Depth is capped; the point is that it returns instead of blowing the stack.
        _ = CommandAnalyzer.Segments(command);
    }
}
