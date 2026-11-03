using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

public class SubAgentToolTests : IDisposable
{
    private const string AgentId = "sub-ephemeral";
    private const string Model = "test-model";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A dry run (ephemeral subagent) rolls the session's usage back to its checkpoint. That rollback
    /// re-inserts the numbers as a single row, so it must carry the model: a row with an empty model
    /// matches no price entry and is silently costed at $0.
    /// </summary>
    [Fact]
    public async Task EphemeralRun_RestoresCheckpointUsage_WithModel()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        await _db.Sessions.RecordUsageAsync(AgentId, 100, 50, 1000, reasoning: 800, model: "prev-model");

        var manager = new SubAgentManager(NullLogger<SubAgentManager>.Instance);
        var (scope, _) = BuildScope();
        var scopeFactory = Substitute.For<IAgentScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => scope);

        var output = await SubAgentTool.RunSubAgentTaskAsync(
            AgentId, "do nothing", ephemeral: true,
            manager, scopeFactory, _db.Sessions, _db.Blocks,
            mainSessionId: null, defaultModel: Model, CancellationToken.None);

        Assert.NotNull(output);

        var restored = Assert.Single(await _db.Sessions.GetUsageByModelAsync(AgentId));
        Assert.Equal(Model, restored.Model);
        Assert.Equal(100, restored.Hit);
        Assert.Equal(50, restored.Miss);
        Assert.Equal(1000, restored.Output);
        Assert.Equal(800, restored.Reasoning);
    }

    /// <summary>
    /// A subagent's cwd is the base directory of the tools it holds: relative read/write/search paths
    /// must resolve there, not in the directory glyphite was launched from. Checked end to end on a
    /// real run before the fix — the files landed in the launch directory instead.
    /// </summary>
    [Fact]
    public async Task Cwd_IsTheBaseDirectoryOfTheSubAgentsTools()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        var manager = new SubAgentManager(NullLogger<SubAgentManager>.Instance);
        var (scope, tools) = BuildScope();
        var scopeFactory = Substitute.For<IAgentScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => scope);

        await SubAgentTool.RunSubAgentTaskAsync(
            AgentId, "do nothing", ephemeral: true,
            manager, scopeFactory, _db.Sessions, _db.Blocks,
            mainSessionId: null, defaultModel: Model, CancellationToken.None,
            cwd: "/tmp/sub-project");

        await tools.Received(1).GetBuiltinToolsAsync(AgentId, Arg.Any<bool>(), "/tmp/sub-project");
    }

    /// <summary>A scope whose turn processor produces no events — the task runs and returns empty.</summary>
    private static (AgentScope Scope, IToolRegistry Tools) BuildScope()
    {
        var turnProcessor = Substitute.For<ITurnProcessor>();
        turnProcessor
            .ProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(_ => NoEvents());

        var tools = Substitute.For<IToolRegistry>();
        tools.GetBuiltinToolsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Task.FromResult<IReadOnlyList<AITool>>([]));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(ITurnProcessor)).Returns(turnProcessor);
        services.GetService(typeof(IToolRegistry)).Returns(tools);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(services);
        return (new AgentScope(scope), tools);
    }

    private static async IAsyncEnumerable<TurnEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}
