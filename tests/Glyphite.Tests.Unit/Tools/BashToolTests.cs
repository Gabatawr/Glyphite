using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

/// <summary>
/// The gate in front of every shell command. Each test here corresponds to a way the old check let a
/// command through: a pattern after a separator, behind a wrapper, a prompt per pattern instead of one
/// per command, or an AI check that failed and answered "safe".
/// </summary>
public class BashToolTests
{
    private const string AgentId = "test-agent";

    private readonly IBashSessionManager _bash = Substitute.For<IBashSessionManager>();
    private readonly ISafetyChecker _safety = Substitute.For<ISafetyChecker>();
    private readonly RecordingConfirmationUi _ui = new();

    public BashToolTests()
    {
        _bash.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("ok"));
        _bash.GetStartDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Directory.GetCurrentDirectory()));
    }

    private static BashOptions Options(string[]? forbidden = null, string[]? check = null) => new()
    {
        ExecutablePath = "/bin/bash",
        DiscoveryTimeoutMs = 1000,
        ForbiddenCommands = forbidden ?? [],
        CheckRequireCommands = check ?? [],
        CheckRequireCommandsTimeout = 10,
    };

    private Task<string> RunAsync(string command, BashOptions options, bool skipConfirm = false)
        => BashTool.ExecuteBash(
            command, workdir: null, timeoutMs: null, _bash, AgentId,
            new ContentDedupOptions(), options,
            kvStore: null, isSubAgent: false, safetyChecker: _safety,
            ct: default, skipInteractiveConfirm: skipConfirm, confirmation: _ui);

    /// <summary>Same as <see cref="RunAsync"/>, but with a working directory and a known shell start.</summary>
    private Task<string> RunInAsync(string command, string workdir, BashOptions options, string? shellStart = null)
    {
        if (shellStart is not null)
            _bash.GetStartDirectoryAsync(AgentId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(shellStart));

        return BashTool.ExecuteBash(
            command, workdir, timeoutMs: null, _bash, AgentId,
            new ContentDedupOptions(), options,
            kvStore: null, isSubAgent: false, safetyChecker: _safety,
            ct: default, skipInteractiveConfirm: true, confirmation: _ui);
    }

    /// <summary>Bash options with the given directory rules and no confirmation patterns.</summary>
    private static BashOptions WithDirectories(params string[] forbiddenDirectories) => new()
    {
        ExecutablePath = "/bin/bash",
        DiscoveryTimeoutMs = 1000,
        ForbiddenDirectories = forbiddenDirectories,
        CheckRequireCommands = [],
        CheckRequireCommandsTimeout = 10,
    };

    private async Task AssertBlockedWithoutExecutingAsync(string result)
    {
        Assert.DoesNotContain("ok", result);
        await _bash.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── Forbidden directories: a path, not a string prefix ──

    /// <summary>
    /// The shipped default list ("/", "/etc", "/boot", "/sys", "/proc", "/dev") used to reject every
    /// absolute workdir, because "/" was trimmed to "" and every absolute path starts with "/".
    /// </summary>
    [Theory]
    [InlineData("/home/gab/project")]
    [InlineData("/tmp/work")]
    [InlineData("/var/www/app")]
    public async Task AbsoluteWorkdir_IsAllowed_WhenTheRootRuleIsPresent(string workdir)
    {
        var result = await RunInAsync("echo hi", workdir,
            WithDirectories("/", "/etc", "/boot", "/sys", "/proc", "/dev"));

        Assert.Equal("ok", result);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/etc/passwd")]
    [InlineData("/proc")]
    public async Task ForbiddenDirectory_BlocksTheRuleAndEverythingBelow(string workdir)
    {
        var result = await RunInAsync("echo hi", workdir,
            WithDirectories("/", "/etc", "/boot", "/sys", "/proc", "/dev"));

        Assert.Contains("Directory forbidden", result);
        await AssertBlockedWithoutExecutingAsync(result);
    }

    [Fact]
    public async Task RelativeWorkdir_IsResolvedFromTheShellStartDirectory()
    {
        // The shell starts in /etc, so "../etc" is /etc again — the rule has to catch it.
        var result = await RunInAsync("echo hi", "../etc", WithDirectories("/etc"), shellStart: "/etc");

        Assert.Contains("Directory forbidden", result);
    }

    [Fact]
    public async Task RelativeWorkdir_BelowTheRules_IsAllowed()
    {
        var result = await RunInAsync("echo hi", "sub", WithDirectories("/etc"),
            shellStart: "/home/gab/project");

        Assert.Equal("ok", result);
    }

    // ── Forbidden commands: found where they actually run ──

    [Theory]
    [InlineData("cd /tmp && rm -rf ~")]
    [InlineData("echo hi; rm -rf ~")]
    [InlineData("FOO=1 rm -rf ~")]
    [InlineData("bash -c \"rm -rf /\"")]
    [InlineData("sudo rm -rf /")]
    [InlineData("/usr/bin/sudo rm -rf /")]
    [InlineData("timeout 30 rm -rf /")]
    public async Task ForbiddenPattern_IsBlockedWhereverItSits(string command)
    {
        var result = await RunAsync(command, Options(forbidden: ["rm -rf"]));

        Assert.Contains("Command forbidden", result);
        await AssertBlockedWithoutExecutingAsync(result);
        Assert.Empty(_ui.Calls); // a hard block never asks
    }

    [Fact]
    public async Task ForbiddenPattern_AsAnArgument_IsNotBlocked()
    {
        var result = await RunAsync("git log --grep \"rm -rf\"", Options(forbidden: ["rm -rf"]));

        Assert.Equal("ok", result);
    }

    // ── One question per command ──

    [Fact]
    public async Task SeveralMatches_StillAskExactlyOnce_AboutTheWholeCommand()
    {
        const string command = "rm -rf /tmp/a && sudo rm -rf /tmp/b";
        _ui.Queue(InteractiveConfirmation.Choice.Ok);

        var result = await RunAsync(command, Options(check: ["rm -rf"]));

        Assert.Equal("ok", result);
        var call = Assert.Single(_ui.Calls);
        Assert.Equal(command, call.Command); // the full string, not a slice
        Assert.False(call.NoAutoSelect);
    }

    [Fact]
    public async Task NoMatch_NoQuestion()
    {
        var result = await RunAsync("ls -la", Options(check: ["rm -rf"]));

        Assert.Equal("ok", result);
        Assert.Empty(_ui.Calls);
    }

    [Fact]
    public async Task Stop_BlocksWithoutExecuting()
    {
        _ui.Queue(InteractiveConfirmation.Choice.Stop);

        var result = await RunAsync("rm -rf /tmp/x", Options(check: ["rm -rf"]));

        Assert.Contains("blocked by user", result);
        await AssertBlockedWithoutExecutingAsync(result);
    }

    [Fact]
    public async Task Ok_Runs_WithoutConsultingTheSafetyChecker()
    {
        _ui.Queue(InteractiveConfirmation.Choice.Ok);

        var result = await RunAsync("rm -rf /tmp/x", Options(check: ["rm -rf"]));

        Assert.Equal("ok", result);
        await _safety.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApprovedTemplate_SkipsTheQuestion()
    {
        var result = await RunAsync("rm -rf /tmp/x", Options(check: ["rm -rf"]), skipConfirm: true);

        Assert.Equal("ok", result);
        Assert.Empty(_ui.Calls);
    }

    // ── Check: a broken checker hands the decision to a human, not to the timer ──

    [Fact]
    public async Task Check_AllowsWhenTheCheckerAllows()
    {
        _ui.Queue(InteractiveConfirmation.Choice.Check);
        _safety.CheckAsync(AgentId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SafetyVerdict(true, "")));

        var result = await RunAsync("rm -rf /tmp/x", Options(check: ["rm -rf"]));

        Assert.Equal("ok", result);
        Assert.Single(_ui.Calls);
    }

    [Fact]
    public async Task Check_BlocksWhenTheCheckerDenies()
    {
        _ui.Queue(InteractiveConfirmation.Choice.Check);
        _safety.CheckAsync(AgentId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SafetyVerdict(false, "destructive")));

        var result = await RunAsync("rm -rf /tmp/x", Options(check: ["rm -rf"]));

        Assert.Contains("blocked by safety check", result);
        Assert.Contains("destructive", result);
        await AssertBlockedWithoutExecutingAsync(result);
    }

    [Fact]
    public async Task CheckInconclusive_AsksTheHuman_WithTheTimerOff()
    {
        _ui.Queue(InteractiveConfirmation.Choice.Check, InteractiveConfirmation.Choice.Ok);
        _safety.CheckAsync(AgentId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SafetyVerdict.Unavailable("llm down")));

        var result = await RunAsync("rm -rf /tmp/x", Options(check: ["rm -rf"]));

        Assert.Equal("ok", result);
        Assert.Equal(2, _ui.Calls.Count);
        Assert.False(_ui.Calls[0].NoAutoSelect);        // the normal first question
        Assert.True(_ui.Calls[1].NoAutoSelect);         // the fallback: no timer, no auto-consent
        Assert.Equal("safety check unavailable", _ui.Calls[1].Note);
    }

    [Fact]
    public async Task CheckInconclusive_HumanDoesNotApprove_Blocks()
    {
        _ui.Queue(InteractiveConfirmation.Choice.Check, InteractiveConfirmation.Choice.Stop);
        _safety.CheckAsync(AgentId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SafetyVerdict.Unavailable("llm down")));

        var result = await RunAsync("rm -rf /tmp/x", Options(check: ["rm -rf"]));

        Assert.Contains("safety check unavailable", result);
        await AssertBlockedWithoutExecutingAsync(result);
    }

    // ── The background path is a command too ──

    [Fact]
    public async Task BackgroundCommand_PassesTheSameGates()
    {
        var cfg = Substitute.For<IConfigService>();
        cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, Arg.Any<string?>())
            .Returns(Task.FromResult(Options(forbidden: ["rm -rf"])));

        var tool = BashTool.AsAIFunction(_bash, AgentId, cfg, kvStore: null, isSubAgent: false, safetyChecker: _safety);

        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "sudo rm -rf /",
            ["back"] = true,
        }));

        Assert.Contains("Command forbidden", result?.ToString());
        await _bash.DidNotReceive().StartBackgroundAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Answers instead of a human and remembers what it was asked.</summary>
    private sealed class RecordingConfirmationUi : IConfirmationUi
    {
        private readonly Queue<InteractiveConfirmation.Choice> _script = new();

        public List<(string Command, bool NoAutoSelect, string? Note)> Calls { get; } = [];

        public void Queue(params InteractiveConfirmation.Choice[] choices)
        {
            foreach (var choice in choices)
                _script.Enqueue(choice);
        }

        public Task<InteractiveConfirmation.Choice> AskAsync(
            string command, int timeoutMs, IKVStore? kvStore, string? agentId, bool isSubAgent,
            bool noAutoSelect, string? note, CancellationToken ct)
        {
            Calls.Add((command, noAutoSelect, note));

            // Default to the safe answer: an unexpected extra question must never mean "yes".
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : InteractiveConfirmation.Choice.Stop);
        }
    }
}
