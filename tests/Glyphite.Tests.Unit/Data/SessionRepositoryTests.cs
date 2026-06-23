using Glyphite.Host.Data;
using Xunit;

namespace Glyphite.Tests.Unit.Data;

public class SessionRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connStr;

    public SessionRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glyphite_test_{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        // Create all tables (sessions, blocks, config) by initializing repositories
        using var sessionRepo = new SessionRepository(_connStr);
        using var blockRepo = new BlockRepository(_connStr);
        using var configRepo = new ConfigRepository(_connStr);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch { /* best-effort cleanup */ }
    }

    private SessionRepository CreateRepo() => new(_connStr);

    [Fact]
    public async Task Can_Create_And_Retrieve_Agent()
    {
        using var repo = CreateRepo();
        const string agentId = "session-test-agent-1";

        await repo.EnsureSessionAsync(agentId, homePath: "/home/test");

        Assert.True(await repo.AgentExistsAsync(agentId));
        Assert.Equal("/home/test", await repo.GetAgentHomePathAsync(agentId));

        var createdAt = await repo.GetAgentCreatedAtAsync(agentId);
        Assert.NotNull(createdAt);
    }

    [Fact]
    public async Task EnsureSessionAsync_Is_Idempotent()
    {
        using var repo = CreateRepo();
        const string agentId = "idempotent-agent";

        await repo.EnsureSessionAsync(agentId);
        await repo.EnsureSessionAsync(agentId); // should not throw

        Assert.True(await repo.AgentExistsAsync(agentId));
    }

    [Fact]
    public async Task Can_Update_Agent_Properties()
    {
        using var repo = CreateRepo();
        const string agentId = "session-update-agent";

        await repo.EnsureSessionAsync(agentId);

        var updated = await repo.SetAgentModelAsync(agentId, "deepseek-chat");
        Assert.True(updated);

        var model = await repo.GetAgentModelAsync(agentId);
        Assert.Equal("deepseek-chat", model);

        await repo.SetAgentModelAsync(agentId, "deepseek-reasoner");
        model = await repo.GetAgentModelAsync(agentId);
        Assert.Equal("deepseek-reasoner", model);

        await repo.SetNextNumberAsync(agentId, 42);
        var nextNumber = await repo.GetNextNumberAsync(agentId);
        Assert.Equal(42, nextNumber);
    }

    [Fact]
    public async Task SetAgentModelAsync_Returns_False_For_Nonexistent()
    {
        using var repo = CreateRepo();
        var result = await repo.SetAgentModelAsync("nonexistent-agent", "deepseek-chat");
        Assert.False(result);
    }

    [Fact]
    public async Task Can_List_Agents()
    {
        using var repo = CreateRepo();

        await repo.EnsureSessionAsync("list-agent-a");
        await repo.EnsureSessionAsync("list-agent-b");
        await repo.EnsureSessionAsync("list-agent-c");

        var agents = await repo.ListAgentsAsync();
        Assert.Contains("list-agent-a", agents);
        Assert.Contains("list-agent-b", agents);
        Assert.Contains("list-agent-c", agents);
    }

    [Fact]
    public async Task Agent_Deletion_Works()
    {
        using var repo = CreateRepo();
        const string agentId = "delete-agent";

        await repo.EnsureSessionAsync(agentId);
        Assert.True(await repo.AgentExistsAsync(agentId));

        await repo.DeleteSessionAsync(agentId);

        Assert.False(await repo.AgentExistsAsync(agentId));

        var agents = await repo.ListAgentsAsync();
        Assert.DoesNotContain(agentId, agents);
    }

    [Fact]
    public async Task Usage_Stats_Tracking()
    {
        using var repo = CreateRepo();
        const string agentId = "usage-agent";

        await repo.EnsureSessionAsync(agentId);

        await repo.RecordUsageAsync(agentId, 100, 50, 1000, 10, 5, "deepseek-chat");

        var usage = await repo.GetUsageAsync(agentId);
        Assert.Equal(100, usage.Hit);
        Assert.Equal(50, usage.Miss);
        Assert.Equal(1000, usage.Output);

        await repo.RecordUsageAsync(agentId, 200, 100, 2000, 20, 10, "deepseek-chat");

        usage = await repo.GetUsageAsync(agentId);
        Assert.Equal(300, usage.Hit);
        Assert.Equal(150, usage.Miss);
        Assert.Equal(3000, usage.Output);

        var lastUsage = await repo.GetLastUsageAsync(agentId);
        Assert.Equal(200, lastUsage.Hit);
        Assert.Equal(100, lastUsage.Miss);
        Assert.Equal(2000, lastUsage.Output);
        Assert.Equal(20, lastUsage.LastHit);
        Assert.Equal(10, lastUsage.LastMiss);
    }

    [Fact]
    public async Task Usage_Stats_By_Model()
    {
        using var repo = CreateRepo();
        const string agentId = "usage-model-agent";

        await repo.EnsureSessionAsync(agentId);

        await repo.RecordUsageAsync(agentId, 100, 50, 1000, model: "model-a");
        await repo.RecordUsageAsync(agentId, 200, 100, 2000, model: "model-b");

        var byModel = await repo.GetUsageByModelAsync(agentId);
        Assert.Equal(2, byModel.Count);

        var modelA = byModel.First(m => m.Model == "model-a");
        Assert.Equal(100, modelA.Hit);
        Assert.Equal(50, modelA.Miss);
        Assert.Equal(1000, modelA.Output);

        var modelB = byModel.First(m => m.Model == "model-b");
        Assert.Equal(200, modelB.Hit);
        Assert.Equal(100, modelB.Miss);
        Assert.Equal(2000, modelB.Output);
    }

    [Fact]
    public async Task ClearUsage_Works()
    {
        using var repo = CreateRepo();
        const string agentId = "usage-clear-agent";

        await repo.EnsureSessionAsync(agentId);
        await repo.RecordUsageAsync(agentId, 100, 50, 1000);

        var before = await repo.GetUsageAsync(agentId);
        Assert.Equal(100, before.Hit);

        await repo.ClearUsageAsync(agentId);

        var after = await repo.GetUsageAsync(agentId);
        Assert.Equal(0, after.Hit);
        Assert.Equal(0, after.Miss);
        Assert.Equal(0, after.Output);
    }

    [Fact]
    public async Task GetUsage_Returns_Zero_For_No_Usage()
    {
        using var repo = CreateRepo();
        const string agentId = "no-usage-agent";

        await repo.EnsureSessionAsync(agentId);

        var usage = await repo.GetUsageAsync(agentId);
        Assert.Equal(0, usage.Hit);
        Assert.Equal(0, usage.Miss);
        Assert.Equal(0, usage.Output);
    }

    [Fact]
    public async Task RecordLaunch_And_GetLaunches_Works()
    {
        using var repo = CreateRepo();
        const string agentId = "launch-agent";

        await repo.EnsureSessionAsync(agentId);

        await repo.RecordLaunchAsync(agentId, "/home/project1");
        await repo.RecordLaunchAsync(agentId, "/home/project2");

        var launches = await repo.GetLaunchesAsync(agentId);
        Assert.Equal(2, launches.Count);
        Assert.Contains(launches, l => l.path == "/home/project1");
        Assert.Contains(launches, l => l.path == "/home/project2");
    }

    [Fact]
    public async Task GetLastLaunchPath_Returns_Most_Recent()
    {
        using var repo = CreateRepo();
        const string agentId = "last-launch-agent";

        await repo.EnsureSessionAsync(agentId);
        await repo.RecordLaunchAsync(agentId, "/home/old");
        await repo.RecordLaunchAsync(agentId, "/home/new");

        var last = await repo.GetLastLaunchPathAsync(agentId);
        Assert.Equal("/home/new", last);
    }

    [Fact]
    public async Task GetLastActiveAgent_Returns_Correct()
    {
        using var repo = CreateRepo();
        const string agentId = "last-active-agent";

        await repo.EnsureSessionAsync(agentId);
        await repo.RecordLaunchAsync(agentId, "/home/shared");

        var active = await repo.GetLastActiveAgentAsync("/home/shared");
        Assert.Equal(agentId, active);
    }

    [Fact]
    public async Task GetAgentHomePath_Returns_Null_For_Nonexistent()
    {
        using var repo = CreateRepo();
        var homePath = await repo.GetAgentHomePathAsync("nonexistent-agent");
        Assert.Null(homePath);
    }

    [Fact]
    public async Task GetAgentModel_Returns_Null_For_Nonexistent()
    {
        using var repo = CreateRepo();
        var model = await repo.GetAgentModelAsync("nonexistent-agent");
        Assert.Null(model);
    }
}
