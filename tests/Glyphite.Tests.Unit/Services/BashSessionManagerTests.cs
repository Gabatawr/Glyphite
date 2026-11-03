using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class BashSessionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BashOptions _opts;
    private readonly IConfigService _cfg = Substitute.For<IConfigService>();
    private readonly BashSessionManager _manager;

    public BashSessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GlyphiteBash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _opts = new BashOptions
        {
            ExecutablePath = "bash",
            DiscoveryTimeoutMs = 2000,
            DefaultDirectory = _tempDir,
        };
        _cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, Arg.Any<string?>()).Returns(_opts);
        _manager = new BashSessionManager(_cfg, _opts, NullLogger<BashSessionManager>.Instance, _tempDir);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
// ── Execute ──

    [Fact]
    public async Task Execute_ReturnsCommandOutput()
    {
        var output = await _manager.ExecuteAsync("agent-basic", "echo hello-world");

        Assert.Contains("hello-world", output);
    }

    /// <summary>
    /// A subagent's own Glyphite.json is its config, so its Bash:DefaultDirectory must be honoured.
    /// Reading BashOptions without the agent id silently pinned every shell to the global section,
    /// which is why a subagent's shell started in the directory glyphite was launched from.
    /// </summary>
    [Fact]
    public async Task Execute_UsesPerAgentDefaultDirectory()
    {
        var agentDir = Path.Combine(_tempDir, "agent-dir");
        Directory.CreateDirectory(agentDir);
        _cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, "agent-with-dir").Returns(new BashOptions
        {
            ExecutablePath = "bash",
            DiscoveryTimeoutMs = 2000,
            DefaultDirectory = agentDir,
        });

        var output = await _manager.ExecuteAsync("agent-with-dir", "pwd");

        Assert.Contains(agentDir, output);
    }

    /// <summary>No explicit default → the agent's home is the starting directory (a subagent's cwd).</summary>
    [Fact]
    public async Task Execute_FallsBackToAgentHome()
    {
        var home = Path.Combine(_tempDir, "agent-home");
        Directory.CreateDirectory(home);
        var agents = Substitute.For<IAgentStore>();
        agents.GetAgentHomePathAsync("agent-in-home").Returns(home);
        var opts = new BashOptions { ExecutablePath = "bash", DiscoveryTimeoutMs = 2000 };
        _cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, "agent-in-home").Returns(opts);
        using var manager = new BashSessionManager(
            _cfg, opts, NullLogger<BashSessionManager>.Instance, _tempDir, agents);

        var output = await manager.ExecuteAsync("agent-in-home", "pwd");

        Assert.Contains(home, output);
    }

    [Fact]
    public async Task Execute_RespectsWorkdir()
    {
        var nested = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nested);

        var output = await _manager.ExecuteAsync("agent-wd", "pwd", workdir: nested);

        Assert.Contains(nested, output);
    }

    [Fact]
    public async Task Execute_Timeout_KillsSession_AndRemovesIt()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _manager.ExecuteAsync("agent-timeout", "sleep 5", timeoutMs: 500));

        Assert.DoesNotContain("agent-timeout", _manager.ActiveSessions);
    }

    [Fact]
    public async Task Execute_ConcurrentCallsOnSameSession_AreSerialized()
    {
        var t1 = _manager.ExecuteAsync("agent-conc", "echo first; sleep 0.3; echo second");
        var t2 = _manager.ExecuteAsync("agent-conc", "echo third");
        var (r1, r2) = (await t1, await t2);

        Assert.Contains("first", r1);
        Assert.Contains("second", r1);
        Assert.Contains("third", r2);
    }
}
