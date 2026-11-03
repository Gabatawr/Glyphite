using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

public class PocketToolTests
{
    private const string AgentId = "test-agent";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── Name / category / scope normalization ──

    [Theory]
    [InlineData("  MyTool  ", "mytool")]
    [InlineData("", "")]
    [InlineData("ALREADY-LOWER", "already-lower")]
    public void NormalizeName_TrimsAndLowercases(string input, string expected)
        => Assert.Equal(expected, PocketTool.NormalizeName(input));

    [Fact]
    public void MaterializedName_AppendsSuffix()
        => Assert.Equal("foo_pocket", PocketTool.MaterializedName("foo"));

    [Fact]
    public void MaterializedName_NoDoubleSuffix()
        => Assert.Equal("foo_pocket", PocketTool.MaterializedName("foo_pocket"));

    [Fact]
    public void MaterializedName_TruncatesTo64Chars()
    {
        var result = PocketTool.MaterializedName(new string('x', 62));
        Assert.Equal(64, result.Length);
        Assert.EndsWith("_pocket", result);
    }

    [Theory]
    [InlineData("", "common")]
    [InlineData("  XYZ  ", "xyz")]
    public void NormalizeCategory_DefaultsAndNormalizes(string input, string expected)
        => Assert.Equal(expected, PocketTool.NormalizeCategory(input));

    [Theory]
    [InlineData("", "local")]
    [InlineData("GLOBAL", "global")]
    [InlineData(" Local ", "local")]
    public void NormalizeScope_DefaultsAndNormalizes(string input, string expected)
        => Assert.Equal(expected, PocketTool.NormalizeScope(input));

    // ── KVStore loading ──

    [Fact]
    public async Task LoadEntriesAsync_SortsByName_AndSkipsCorrupt()
    {
        var kv = Substitute.For<IKVStore>();
        kv.ListAsync(AgentId, "pocket.*").Returns(new Dictionary<string, string>
        {
            ["pocket.z"] = ToJson(Entry("z")),
            ["pocket.a"] = ToJson(Entry("a")),
            ["pocket.bad"] = "corrupt json",
        });

        var entries = await PocketTool.LoadEntriesAsync(kv, AgentId);

        Assert.Equal(new[] { "a", "z" }, entries.Select(e => e.Name));
    }

    [Fact]
    public async Task GetEntryAsync_ReturnsNull_OnCorruptJson()
    {
        var kv = Substitute.For<IKVStore>();
        kv.GetAsync(AgentId, "pocket.bad").Returns("corrupt");

        var entry = await PocketTool.GetEntryAsync(kv, AgentId, "bad");

        Assert.Null(entry);
    }

    [Fact]
    public async Task GetEffectiveEntryAsync_LocalShadowsGlobal()
    {
        var kv = Substitute.For<IKVStore>();
        kv.GetAsync(AgentId, "pocket.tool").Returns(ToJson(Entry("tool", cmd: "local cmd")));
        kv.GetAsync(PocketTool.GlobalAgentId, "pocket.tool").Returns(ToJson(Entry("tool", cmd: "global cmd")));

        var entry = await PocketTool.GetEffectiveEntryAsync(kv, AgentId, "tool");

        Assert.NotNull(entry);
        Assert.Equal("local cmd", entry!.Cmd);
    }

    [Fact]
    public async Task GetEffectiveEntryAsync_FallsBackToGlobal()
    {
        var kv = Substitute.For<IKVStore>();
        kv.GetAsync(AgentId, "pocket.tool").Returns((string?)null);
        kv.GetAsync(PocketTool.GlobalAgentId, "pocket.tool").Returns(ToJson(Entry("tool", cmd: "global cmd")));

        var entry = await PocketTool.GetEffectiveEntryAsync(kv, AgentId, "tool");

        Assert.NotNull(entry);
        Assert.Equal("global cmd", entry!.Cmd);
    }

    // ── Runner: substitution & execution ──

    [Fact]
    public async Task Runner_MissingRequiredArg_ReturnsError_NoExecution()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = Entry("t", "cat {file}", MakeArg("file", req: true));

        var result = await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Contains("missing required arg", result);
        Assert.Contains("file", result);
        await bash.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_ShellQuotesValueWithSpaces()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = Entry("t", "cat {file}", MakeArg("file"));

        var result = await runner(entry, new Dictionary<string, object?> { ["file"] = "a b" }, CancellationToken.None);

        Assert.Equal("ok", result);
        await bash.Received(1).ExecuteAsync(AgentId, "cat 'a b'", null, 120_000, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_RawArg_InsertedWithoutQuoting()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = Entry("t", "cat {file}", MakeArg("file", raw: true));

        await runner(entry, new Dictionary<string, object?> { ["file"] = "a b" }, CancellationToken.None);

        await bash.Received(1).ExecuteAsync(AgentId, "cat a b", Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_OptionalArg_UsesDefault()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = Entry("t", "cat {file}", MakeArg("file", def: "default.txt"));

        await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        await bash.Received(1).ExecuteAsync(AgentId, "cat default.txt", Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_UsesEntryCwd()
    {
        var (runner, bash, _) = CreateRunnerHarness();
        var entry = Entry("t", "pwd");
        entry.Cwd = "/tmp/project";

        await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        await bash.Received(1).ExecuteAsync(AgentId, "pwd", "/tmp/project", Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── Approval covers the template, not whatever gets substituted into it ──

    [Fact]
    public async Task Runner_ApprovedEntry_WithSubstitution_GoesThroughTheGatesAgain()
    {
        var (runner, _, safety) = CreateRunnerHarness(checkRequired: ["deploy"]);
        var entry = Entry("t", "deploy {env}", MakeArg("env"));

        await runner(entry, new Dictionary<string, object?> { ["env"] = "prod" }, CancellationToken.None);

        // The entry was approved as `deploy {env}` at add time; `deploy prod` is a different command,
        // carrying a value the model chose, so it is checked at run time like a hand-written call.
        await safety.Received(1).CheckAsync(AgentId, "deploy prod", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runner_ApprovedEntry_WithoutSubstitution_SkipsTheGates()
    {
        var (runner, _, safety) = CreateRunnerHarness(checkRequired: ["deploy"]);
        var entry = Entry("t", "deploy");

        await runner(entry, new Dictionary<string, object?>(), CancellationToken.None);

        // Nothing was substituted: the command is exactly what the human approved once.
        await safety.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──

    private static (PocketTool.PocketRunner Runner, IBashSessionManager Bash, ISafetyChecker Safety) CreateRunnerHarness(
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
        var runner = PocketTool.CreateRunner(bash, cfg, kv, isSubAgent: false, safetyChecker: safety, agentId: AgentId);
        return (runner, bash, safety);
    }

    private static PocketTool.PocketEntry Entry(string name, string cmd = "echo hi", params PocketTool.PocketArg[] args) => new()
    {
        Name = name,
        Desc = "test entry",
        Cmd = cmd,
        Args = args.ToList(),
        Approved = true,
    };

    private static PocketTool.PocketArg MakeArg(string name, bool req = false, string? def = null, bool raw = false) => new()
    {
        Name = name,
        Desc = "arg",
        Req = req,
        Def = def,
        Raw = raw,
    };

    private static string ToJson(PocketTool.PocketEntry entry)
        => JsonSerializer.Serialize(entry, JsonOpts);
}
