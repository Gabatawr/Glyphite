using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

public class KVStoreToolTests
{
    private readonly IKVStore _kv = Substitute.For<IKVStore>();
    private readonly IConfigService _cfg = Substitute.For<IConfigService>();
    private readonly SubAgentManager _subAgents = new();
    // Unique per test instance → no cross-test contamination of static pending ops / overlays
    private readonly string _agentId = $"agent-{Guid.NewGuid():N}";

    private Task<string> Execute(string action, string? key = null, string? value = null, int? ttl = null, string scope = "vault")
        => KVStoreTool.Execute(action, key, value, ttl, scope, _kv, _cfg, _subAgents, _agentId);

    // ── Dispatch ──

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var result = await Execute("bogus");

        Assert.Contains("Unknown action 'bogus'", result);
    }

    // ── Vault set ──

    [Fact]
    public async Task Set_Vault_NoWildcard_WritesValue()
    {
        var result = await Execute("set", "k", "v");

        Assert.Equal("set: 'k' = 'v'", result);
        await _kv.Received(1).SetAsync(_agentId, "k", "v", Arg.Any<int?>());
    }

    [Fact]
    public async Task Set_Vault_EmptyValue_Deletes()
    {
        var result = await Execute("set", "k", "");

        Assert.Equal("set: 'k' = deleted", result);
        await _kv.Received(1).SetAsync(_agentId, "k", "", Arg.Any<int?>());
    }

    [Fact]
    public async Task Set_Vault_WithTtl_ForwardsTtl()
    {
        await Execute("set", "k", "v", ttl: 60);

        await _kv.Received(1).SetAsync(_agentId, "k", "v", 60);
    }

    [Fact]
    public async Task Set_MissingKey_ReturnsError()
    {
        var result = await Execute("set");

        Assert.Contains("'key' is required", result);
        await _kv.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>());
    }

    // ── Vault get ──

    [Fact]
    public async Task Get_Vault_ReturnsFormattedEntries()
    {
        _kv.ListAsync(_agentId, "k*").Returns(new Dictionary<string, string> { ["k1"] = "v1", ["k2"] = "v2" });

        var result = await Execute("get", "k*");

        Assert.Contains("k1 = v1", result);
        Assert.Contains("k2 = v2", result);
    }

    [Fact]
    public async Task Get_Vault_StripsEmptyValues()
    {
        _kv.ListAsync(_agentId, "*").Returns(new Dictionary<string, string> { ["keep"] = "x", ["gone"] = "" });

        var result = await Execute("get", "*");

        Assert.Contains("keep = x", result);
        Assert.DoesNotContain("gone", result);
    }

    [Fact]
    public async Task Get_Vault_Empty_ReturnsMessage()
    {
        _kv.ListAsync(_agentId, Arg.Any<string?>()).Returns(new Dictionary<string, string>());

        var noKey = await Execute("get");
        Assert.Contains("(empty — no keys stored)", noKey);

        var withKey = await Execute("get", "nope*");
        Assert.Contains("(no keys matching 'nope*')", withKey);
    }

    // ── Config scope ──

    [Fact]
    public async Task Set_Config_WithTtl_Rejected()
    {
        var result = await Execute("set", "k", "v", ttl: 10, scope: "config");

        Assert.Contains("TTL is only supported for 'vault'", result);
        await _cfg.DidNotReceive().UpdateConfigAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Config_WritesSessionConfig()
    {
        var result = await Execute("set", "Llm:ApiKey", "secret", scope: "config");

        Assert.Equal("set: config 'Llm:ApiKey' = 'secret'", result);
        await _cfg.Received(1).UpdateConfigAsync(
            Arg.Is<Dictionary<string, string>>(d => d["Llm:ApiKey"] == "secret"),
            "session", _agentId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Config_EmptyValue_Deletes()
    {
        await Execute("set", "Llm:ApiKey", "", scope: "config");

        await _cfg.Received(1).DeleteConfigAsync(Arg.Is<string[]>(a => a.SequenceEqual(new[] { "Llm:ApiKey" })), "session", _agentId);
    }

    [Fact]
    public async Task Get_Config_FiltersByKey()
    {
        _cfg.GetConfigAsync(_agentId).Returns(new Dictionary<string, string>
        {
            ["Llm:ApiKey"] = "secret",
            ["Bash:ExecutablePath"] = "/bin/bash",
        });

        var result = await Execute("get", "Llm:*", scope: "config");

        Assert.Contains("Llm:ApiKey = secret", result);
        Assert.DoesNotContain("Bash", result);
    }

    // ── Masked set + accept ──

    [Fact]
    public async Task Set_Masked_ShowsDryRun_RequiresAccept()
    {
        _kv.ListAsync(_agentId, "a*").Returns(new Dictionary<string, string> { ["a1"] = "v1", ["a2"] = "v2" });

        var result = await Execute("set", "a*", "new");

        Assert.Contains("Dry-run", result);
        Assert.Contains("Confirm with action='accept'", result);
        Assert.Contains("a1 = v1", result);
        await _kv.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task Set_Masked_NoMatches_ReturnsMessage()
    {
        _kv.ListAsync(_agentId, "zzz*").Returns(new Dictionary<string, string>());

        var result = await Execute("set", "zzz*", "v");

        Assert.Contains("nothing to update", result);
    }

    [Fact]
    public async Task Accept_WithoutPending_ReturnsMessage()
    {
        var result = await Execute("accept");

        Assert.Contains("Nothing to accept", result);
    }

    [Fact]
    public async Task Accept_AfterMaskedSet_AppliesToAllMatchingKeys()
    {
        _kv.ListAsync(_agentId, "a*").Returns(new Dictionary<string, string> { ["a1"] = "v1", ["a2"] = "v2" });
        await Execute("set", "a*", "new");

        var result = await Execute("accept");

        Assert.Contains("accept: set value on 2 key(s): a1, a2", result);
        await _kv.Received(1).SetAsync(_agentId, "a1", "new", Arg.Any<int?>());
        await _kv.Received(1).SetAsync(_agentId, "a2", "new", Arg.Any<int?>());
    }

    [Fact]
    public async Task PlainSet_ClearsPendingAccept()
    {
        _kv.ListAsync(_agentId, "a*").Returns(new Dictionary<string, string> { ["a1"] = "v1" });
        await Execute("set", "a*", "new");
        await Execute("set", "other", "x"); // clears pending

        var result = await Execute("accept");

        Assert.Contains("Nothing to accept", result);
    }

    // ── Ephemeral overlay ──

    [Fact]
    public async Task Ephemeral_SetVault_UsesOverlay_NotPersisted()
    {
        _subAgents.SetEphemeral(_agentId);

        var result = await Execute("set", "k", "ephemeral-value");

        Assert.Contains("(ephemeral)", result);
        await _kv.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>());

        // Overlay is visible on get
        _kv.ListAsync(_agentId, "k").Returns(new Dictionary<string, string>());
        var get = await Execute("get", "k");
        Assert.Contains("k = ephemeral-value", get);
    }

    [Fact]
    public async Task Ephemeral_MaskedSetAccept_AppliesToOverlay()
    {
        _subAgents.SetEphemeral(_agentId);
        _kv.ListAsync(_agentId, "a*").Returns(new Dictionary<string, string> { ["a1"] = "v1", ["a2"] = "v2" });
        await Execute("set", "a*", "new");

        var result = await Execute("accept");

        Assert.Contains("accept: set value on 2 key(s)", result);
        await _kv.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>());

        _kv.ListAsync(_agentId, "*").Returns(new Dictionary<string, string>());
        var get = await Execute("get", "*");
        Assert.Contains("a1 = new", get);
        Assert.Contains("a2 = new", get);
    }

    [Fact]
    public async Task Ephemeral_SetConfig_UsesOverlay()
    {
        _subAgents.SetEphemeral(_agentId);
        _cfg.GetConfigAsync(_agentId).Returns(new Dictionary<string, string>());

        var result = await Execute("set", "Some:Key", "overlay", scope: "config");

        Assert.Contains("(ephemeral)", result);
        _cfg.Received(1).SetSessionOverlay(_agentId, Arg.Is<Dictionary<string, string>>(d => d["Some:Key"] == "overlay"));
        await _cfg.DidNotReceive().UpdateConfigAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
