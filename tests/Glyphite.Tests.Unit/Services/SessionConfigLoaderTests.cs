using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class SessionConfigLoaderTests : IDisposable
{
    private const string AgentId = "test-agent";

    private readonly string _root;
    private readonly string _home;
    private readonly string _parent;
    private readonly string _cwd;

    private readonly IConfigService _cfg = Substitute.For<IConfigService>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IConfigStore _store = Substitute.For<IConfigStore>();

    public SessionConfigLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"glyphite_cfg_{Guid.NewGuid():N}");
        _home = Path.Combine(_root, "home");
        _parent = Path.Combine(_root, "parent");
        _cwd = Path.Combine(_root, "cwd");

        _agents.GetAgentHomePathAsync(AgentId).Returns(_home);
        _store.GetMergedConfigAsync(AgentId).Returns(new Dictionary<string, string>());
        _store.GetMergedConfigAsync(null).Returns(new Dictionary<string, string>());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    private SessionConfigLoader CreateLoader() => new(_cfg, _agents, _store, NullLogger<SessionConfigLoader>.Instance);

    private Task LoadAsync(string? agentCwd = null, string? parentCwd = null)
        => CreateLoader().LoadConfigAsync(AgentId, agentCwd ?? _cwd, parentCwd ?? _parent);

    private static void WriteGlyphite(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Glyphite.json"), json);
    }

    // ── STEP 0: home directory migration ──

    [Fact]
    public async Task HomeDeleted_MigratesToCwd_AndClearsStaleSessionKeys()
    {
        Directory.CreateDirectory(_cwd);
        Directory.CreateDirectory(_parent);
        _agents.GetAgentHomePathAsync(AgentId).Returns(Path.Combine(_root, "deleted-home"));

        await LoadAsync();

        await _agents.Received(1).SetAgentHomePathAsync(AgentId, _cwd);
        await _store.Received(1).DeleteConfigByScopeAsync("session", AgentId);
    }

    // ── STEP 1: home config change detection ──

    [Fact]
    public async Task HomeConfigChanged_UpdatesDb()
    {
        WriteGlyphite(_home, """{"Glyphite": {"Llm": {"ApiKey": "new-key"}}}""");
        _store.GetMergedConfigAsync(AgentId).Returns(new Dictionary<string, string> { ["Llm:ApiKey"] = "old-key" });

        await LoadAsync();

        await _store.Received(1).DeleteConfigByScopeAsync("session", AgentId);
        await _cfg.Received(1).UpdateConfigAsync(
            Arg.Is<Dictionary<string, string>>(d => d.Count == 1 && d["Llm:ApiKey"] == "new-key"),
            "session", AgentId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HomeConfigUnchanged_NoDbWrite()
    {
        WriteGlyphite(_home, """{"Glyphite": {"Llm": {"ApiKey": "same"}}}""");
        _store.GetMergedConfigAsync(AgentId).Returns(new Dictionary<string, string> { ["Llm:ApiKey"] = "same" });

        await LoadAsync();

        await _cfg.DidNotReceive().UpdateConfigAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().DeleteConfigByScopeAsync("session", AgentId);
    }

    // ── STEP 2: merge order ──

    [Fact]
    public async Task MergeOrder_CwdOverridesParentOverridesDb()
    {
        Directory.CreateDirectory(_home);
        WriteGlyphite(_parent, """{"Glyphite": {"A": {"Db": "parent"}, "B": {"Parent": "parent"}}}""");
        WriteGlyphite(_cwd, """{"Glyphite": {"A": {"Db": "cwd"}}}""");
        _store.GetMergedConfigAsync(AgentId).Returns(new Dictionary<string, string> { ["A:Db"] = "db" });

        await LoadAsync();

        _cfg.Received(1).SetSessionOverlay(AgentId, Arg.Is<Dictionary<string, string>>(d =>
            d["A:Db"] == "cwd" && d["B:Parent"] == "parent"));
    }

    [Fact]
    public async Task CwdEqualsParent_AppliesParentConfigOnce()
    {
        Directory.CreateDirectory(_home);
        WriteGlyphite(_parent, """{"Glyphite": {"K": "v"}}""");

        await CreateLoader().LoadConfigAsync(AgentId, _parent, _parent);

        _cfg.Received(1).SetSessionOverlay(AgentId, Arg.Is<Dictionary<string, string>>(d => d["K"] == "v"));
    }

    // ── STEP 3: overlay ──

    [Fact]
    public async Task AtHome_NoOverlaySet()
    {
        Directory.CreateDirectory(_home);

        await CreateLoader().LoadConfigAsync(AgentId, _home, _parent);

        _cfg.DidNotReceive().SetSessionOverlay(Arg.Any<string>(), Arg.Any<Dictionary<string, string>>());
    }

    // ── Config flattening ──

    [Fact]
    public async Task Flatten_NestedObjectArrayBoolNumber()
    {
        Directory.CreateDirectory(_home);
        WriteGlyphite(_cwd, """{"Glyphite": {"A": {"B": "x"}, "arr": ["p", "q"], "flag": true, "num": 42}}""");

        await LoadAsync();

        _cfg.Received(1).SetSessionOverlay(AgentId, Arg.Is<Dictionary<string, string>>(d =>
            d["A:B"] == "x" && d["arr:0"] == "p" && d["arr:1"] == "q" && d["flag"] == "True" && d["num"] == "42"));
    }

    [Fact]
    public async Task MalformedConfigJson_DoesNotCrash()
    {
        Directory.CreateDirectory(_home);
        WriteGlyphite(_parent, "not json {{{");

        await LoadAsync(); // must not throw

        _cfg.Received(1).SetSessionOverlay(AgentId, Arg.Any<Dictionary<string, string>>());
    }
}
