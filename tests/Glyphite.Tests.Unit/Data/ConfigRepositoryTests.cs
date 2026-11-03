using Glyphite.Host.Data;
using Glyphite.Tests.Unit.Support;
using Xunit;

namespace Glyphite.Tests.Unit.Data;

public class ConfigRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ConfigRepository _repo;

    public ConfigRepositoryTests() => _repo = new ConfigRepository(_db.ConnStr);

    public void Dispose()
    {
        _repo.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task GetSessionConfigAsync_ReturnsOnlyThisAgentsSessionRows()
    {
        await _repo.UpsertConfigAsync("Bash:DefaultDirectory", "/global", "global");
        await _repo.UpsertConfigAsync("Bash:DefaultDirectory", "/mine", "session", "agent-1");
        await _repo.UpsertConfigAsync("WebFetch:UserAgent", "mine", "session", "agent-1");
        await _repo.UpsertConfigAsync("WebFetch:UserAgent", "theirs", "session", "agent-2");

        var session = await _repo.GetSessionConfigAsync("agent-1");

        Assert.Equal(2, session.Count);
        // A session row that shadows a global key must survive: subtracting a global dictionary (the
        // previous approach) dropped exactly this case, silently losing the agent's own setting.
        Assert.Equal("/mine", session["Bash:DefaultDirectory"]);
        Assert.Equal("mine", session["WebFetch:UserAgent"]);
    }

    [Fact]
    public async Task GetSessionConfigAsync_UnknownAgent_ReturnsEmpty()
    {
        var session = await _repo.GetSessionConfigAsync("nobody");

        Assert.Empty(session);
    }
}
