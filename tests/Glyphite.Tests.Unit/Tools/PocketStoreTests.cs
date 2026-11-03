using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

/// <summary>
/// The vault behind pocket tools: scopes (local vs the shared sentinel), key layout, and the stored
/// JSON shape. A local entry shadows a global one with the same name.
/// </summary>
public class PocketStoreTests
{
    private const string AgentId = "test-agent";

    [Theory]
    [InlineData("", "local")]
    [InlineData("GLOBAL", "global")]
    [InlineData(" Local ", "local")]
    public void NormalizeScope_DefaultsAndNormalizes(string input, string expected)
        => Assert.Equal(expected, PocketStore.NormalizeScope(input));

    [Theory]
    [InlineData(PocketStore.ScopeLocal, true)]
    [InlineData(PocketStore.ScopeGlobal, true)]
    [InlineData("shared", false)]
    [InlineData("", false)]
    public void IsValidScope_AcceptsOnlyTheTwoScopes(string scope, bool valid)
        => Assert.Equal(valid, PocketStore.IsValidScope(scope));

    [Fact]
    public void NamespaceFor_GlobalEntriesLiveUnderTheSentinel()
    {
        Assert.Equal(PocketStore.GlobalAgentId, PocketStore.NamespaceFor(PocketStore.ScopeGlobal, AgentId));
        Assert.Equal(AgentId, PocketStore.NamespaceFor(PocketStore.ScopeLocal, AgentId));
    }

    // ── Reading ──

    [Fact]
    public async Task LoadEntriesAsync_SortsByName_AndSkipsCorrupt()
    {
        var kv = Substitute.For<IKVStore>();
        kv.ListAsync(AgentId, "pocket.*").Returns(new Dictionary<string, string>
        {
            ["pocket.z"] = PocketFixtures.ToJson(PocketFixtures.Entry("z")),
            ["pocket.a"] = PocketFixtures.ToJson(PocketFixtures.Entry("a")),
            ["pocket.bad"] = "corrupt json",
        });

        var entries = await PocketStore.LoadEntriesAsync(kv, AgentId);

        Assert.Equal(new[] { "a", "z" }, entries.Select(e => e.Name));
    }

    [Fact]
    public async Task GetEntryAsync_ReturnsNull_OnCorruptJson()
    {
        var kv = Substitute.For<IKVStore>();
        kv.GetAsync(AgentId, "pocket.bad").Returns("corrupt");

        var entry = await PocketStore.GetEntryAsync(kv, AgentId, "bad");

        Assert.Null(entry);
    }

    [Fact]
    public async Task GetEffectiveEntryAsync_LocalShadowsGlobal()
    {
        var kv = Substitute.For<IKVStore>();
        kv.GetAsync(AgentId, "pocket.tool").Returns(PocketFixtures.ToJson(PocketFixtures.Entry("tool", cmd: "local cmd")));
        kv.GetAsync(PocketStore.GlobalAgentId, "pocket.tool").Returns(PocketFixtures.ToJson(PocketFixtures.Entry("tool", cmd: "global cmd")));

        var entry = await PocketStore.GetEffectiveEntryAsync(kv, AgentId, "tool");

        Assert.NotNull(entry);
        Assert.Equal("local cmd", entry!.Cmd);
    }

    [Fact]
    public async Task GetEffectiveEntryAsync_FallsBackToGlobal()
    {
        var kv = Substitute.For<IKVStore>();
        kv.GetAsync(AgentId, "pocket.tool").Returns((string?)null);
        kv.GetAsync(PocketStore.GlobalAgentId, "pocket.tool").Returns(PocketFixtures.ToJson(PocketFixtures.Entry("tool", cmd: "global cmd")));

        var entry = await PocketStore.GetEffectiveEntryAsync(kv, AgentId, "tool");

        Assert.NotNull(entry);
        Assert.Equal("global cmd", entry!.Cmd);
    }

    // ── Writing ──

    [Fact]
    public async Task SaveAsync_WritesUnderThePocketKey_InTheGivenNamespace()
    {
        var kv = Substitute.For<IKVStore>();
        var entry = PocketFixtures.Entry("deploy", "echo {env}", PocketFixtures.Arg("env"));

        await PocketStore.SaveAsync(kv, AgentId, entry);

        await kv.Received(1).SetAsync(
            AgentId,
            "pocket.deploy",
            Arg.Is<string>(json => json.Contains("\"name\":\"deploy\"") && json.Contains("\"cmd\":\"echo {env}\"")),
            null);
    }

    [Fact]
    public async Task SaveAsync_GlobalEntry_GoesToTheSentinelNamespace()
    {
        var kv = Substitute.For<IKVStore>();

        await PocketStore.SaveAsync(kv, PocketStore.NamespaceFor(PocketStore.ScopeGlobal, AgentId), PocketFixtures.Entry("shared"));

        await kv.Received(1).SetAsync(PocketStore.GlobalAgentId, "pocket.shared", Arg.Any<string>(), null);
    }

    [Fact]
    public async Task DeleteAsync_RemovesByPocketKey()
    {
        var kv = Substitute.For<IKVStore>();

        await PocketStore.DeleteAsync(kv, PocketStore.GlobalAgentId, "deploy");

        await kv.Received(1).DeleteAsync(PocketStore.GlobalAgentId, "pocket.deploy");
    }

    /// <summary>
    /// The stored JSON is a format, not an implementation detail: entries saved by an older build must
    /// still load. This pins every field that travels through the vault.
    /// </summary>
    [Fact]
    public void StoredJson_RoundTripsEveryField()
    {
        var original = PocketFixtures.Entry("deploy", "echo {env}", PocketFixtures.Arg("env", req: true, def: "prod", raw: true, desc: "target env"));
        original.Cwd = "/tmp/project";
        original.Category = "ops";
        original.Materialize = true;
        original.Created = "2026-11-03T00:00:00.0000000Z";
        original.Approved = false;

        var back = JsonSerializer.Deserialize<PocketEntry>(PocketFixtures.ToJson(original), PocketStore.JsonOpts)!;

        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Desc, back.Desc);
        Assert.Equal(original.Cmd, back.Cmd);
        Assert.Equal(original.Cwd, back.Cwd);
        Assert.Equal(original.Src, back.Src);
        Assert.Equal(original.Category, back.Category);
        Assert.Equal(original.Created, back.Created);
        Assert.Equal(original.Approved, back.Approved);
        Assert.Equal(original.Materialize, back.Materialize);
        var arg = Assert.Single(back.Args);
        Assert.Equal("env", arg.Name);
        Assert.Equal("target env", arg.Desc);
        Assert.True(arg.Req);
        Assert.Equal("prod", arg.Def);
        Assert.True(arg.Raw);
    }
}
