using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

/// <summary>
/// The runner: substitution plus the gates a pocket run goes through. Approval at add time covers the
/// template as written — a substituted command is a different command.
/// </summary>
public class PocketExecutionTests
{
    private const string AgentId = "test-agent";

    [Fact]
    public async Task Runner_MissingRequiredArg_ReturnsError_NoExecution()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = PocketFixtures.Entry("t", "cat {file}", PocketFixtures.Arg("file", req: true));

        var result = await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Contains("missing required arg", result);
        Assert.Contains("file", result);
        await bash.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_ShellQuotesValueWithSpaces()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = PocketFixtures.Entry("t", "cat {file}", PocketFixtures.Arg("file"));

        var result = await runner(entry, new Dictionary<string, object?> { ["file"] = "a b" }, CancellationToken.None);

        Assert.Equal("ok", result);
        await bash.Received(1).ExecuteAsync(AgentId, "cat 'a b'", null, 120_000, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_RawArg_InsertedWithoutQuoting()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = PocketFixtures.Entry("t", "cat {file}", PocketFixtures.Arg("file", raw: true));

        await runner(entry, new Dictionary<string, object?> { ["file"] = "a b" }, CancellationToken.None);

        await bash.Received(1).ExecuteAsync(AgentId, "cat a b", Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_OptionalArg_UsesDefault()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = PocketFixtures.Entry("t", "cat {file}", PocketFixtures.Arg("file", def: "default.txt"));

        await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        await bash.Received(1).ExecuteAsync(AgentId, "cat default.txt", Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_UsesEntryCwd()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = PocketFixtures.Entry("t", "pwd");
        entry.Cwd = "/tmp/project";

        await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        await bash.Received(1).ExecuteAsync(AgentId, "pwd", "/tmp/project", Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_ApprovedEntry_WithSubstitution_GoesThroughTheGatesAgain()
    {
        var (runner, _, safety) = CreateRunnerHarness(checkRequired: ["deploy"]);
        var entry = PocketFixtures.Entry("t", "deploy {env}", PocketFixtures.Arg("env"));

        await runner(entry, new Dictionary<string, object?> { ["env"] = "prod" }, CancellationToken.None);

        // The entry was approved as `deploy {env}` at add time; `deploy prod` is a different command,
        // carrying a value the model chose, so it is checked at run time like a hand-written call.
        await safety.Received(1).CheckAsync(AgentId, "deploy prod", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_ApprovedEntry_WithoutSubstitution_SkipsTheGates()
    {
        var (runner, _, safety) = CreateRunnerHarness(checkRequired: ["deploy"]);
        var entry = PocketFixtures.Entry("t", "deploy");

        await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        // Nothing was substituted: the command is exactly what the human approved once.
        await safety.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──

    private static (PocketExecution.PocketRunner Runner, IBashSessionManager Bash, ISafetyChecker Safety) CreateRunnerHarness(
        string[]? checkRequired = null)
    {
        var bash = Substitute.For<IBashSessionManager>();
        bash.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("ok");

        var safety = Substitute.For<ISafetyChecker>();
        safety.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SafetyVerdict(true, "")));

        var cfg = Substitute.For<IConfigService>();
        cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, Arg.Any<string?>())
            .Returns(new BashOptions
            {
                CheckRequireCommands = checkRequired ?? [],
                // 0s: when a test does trigger the prompt, it must resolve at once instead of waiting
                // for a keystroke that will never come under a redirected console.
                CheckRequireCommandsTimeout = 0,
            });
        cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, Arg.Any<string?>()).Returns(new ContentDedupOptions());
        var kv = Substitute.For<IKVStore>();
        var runner = PocketExecution.CreateRunner(bash, cfg, kv, isSubAgent: false, safetyChecker: safety, agentId: AgentId);
        return (runner, bash, safety);
    }
}
