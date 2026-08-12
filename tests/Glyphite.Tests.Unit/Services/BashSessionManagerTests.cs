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
        _cfg.GetOptionsAsync<BashOptions>(BashOptions.Section).Returns(_opts);
        _manager = new BashSessionManager(_cfg, _opts, NullLogger<BashSessionManager>.Instance, _tempDir);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Execute_ReturnsCommandOutput()
    {
        var output = await _manager.ExecuteAsync("agent-basic", "echo hello-world");

        Assert.Contains("hello-world", output);
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
