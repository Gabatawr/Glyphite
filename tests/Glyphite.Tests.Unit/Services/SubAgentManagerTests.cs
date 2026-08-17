using System.Diagnostics;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class SubAgentManagerTests
{
    private static AgentScope CreateScope()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new AgentScope(provider.CreateScope());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
    }

    // ── Ephemeral flags ──

    [Fact]
    public void IsEphemeral_DefaultFalse()
    {
        var manager = new SubAgentManager();
        Assert.False(manager.IsEphemeral("agent"));
    }

    [Fact]
    public void SetEphemeral_True_ThenFalse()
    {
        var manager = new SubAgentManager();
        manager.SetEphemeral("agent");
        Assert.True(manager.IsEphemeral("agent"));

        manager.SetEphemeral("agent", false);
        Assert.False(manager.IsEphemeral("agent"));
    }

    // ── Scope registration ──

    [Fact]
    public void TryRegister_ReturnsTrue_GetScopeReturnsScope()
    {
        var manager = new SubAgentManager();
        var scope = CreateScope();

        Assert.True(manager.TryRegister("agent", scope));
        Assert.Same(scope, manager.GetScope("agent"));
        Assert.True(manager.Exists("agent"));
    }

    [Fact]
    public void TryRegister_Duplicate_ReturnsFalse_KeepsFirst()
    {
        var manager = new SubAgentManager();
        var first = CreateScope();
        var second = CreateScope();

        Assert.True(manager.TryRegister("agent", first));
        Assert.False(manager.TryRegister("agent", second));
        Assert.Same(first, manager.GetScope("agent"));
    }

    [Fact]
    public void GetScope_NotRegistered_ReturnsNull()
    {
        var manager = new SubAgentManager();
        Assert.Null(manager.GetScope("agent"));
        Assert.False(manager.Exists("agent"));
    }

    [Fact]
    public void Remove_RemovesScope_AndClearsEphemeralFlag()
    {
        var manager = new SubAgentManager();
        manager.TryRegister("agent", CreateScope());
        manager.SetEphemeral("agent");

        manager.Remove("agent");

        Assert.False(manager.Exists("agent"));
        Assert.False(manager.IsEphemeral("agent"));
    }

    // ── RunAsync ──

    [Fact]
    public async Task RunAsync_NotRegistered_Throws()
    {
        var manager = new SubAgentManager();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RunAsync("agent", _ => Task.FromResult("x")));

        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public async Task RunAsync_Executes_AndReturnsResult()
    {
        var manager = new SubAgentManager();
        manager.TryRegister("agent", CreateScope());

        var result = await manager.RunAsync("agent", _ => Task.FromResult("hello"));

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task RunAsync_SerializesConcurrentExecutions()
    {
        var manager = new SubAgentManager();
        manager.TryRegister("agent", CreateScope());

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var overlap = 0;

        Task<string> Call(int id) => manager.RunAsync("agent", async _ =>
        {
            Interlocked.Increment(ref entered);
            if (Volatile.Read(ref entered) > 1)
                Interlocked.Increment(ref overlap);
            await release.Task;
            Interlocked.Decrement(ref entered);
            return $"result-{id}";
        });

        var t1 = Call(1);
        await WaitUntilAsync(() => Volatile.Read(ref entered) == 1);

        var t2 = Call(2);
        await Task.Delay(100);
        // Second call must be blocked by the semaphore
        Assert.Equal(1, Volatile.Read(ref entered));

        release.SetResult();
        Assert.Equal("result-1", await t1);
        Assert.Equal("result-2", await t2);

        Assert.Equal(0, Volatile.Read(ref entered));
        Assert.Equal(0, Volatile.Read(ref overlap));
    }

    // ── ListAll ──

    [Fact]
    public void ListAll_ReturnsRegisteredAgents()
    {
        var manager = new SubAgentManager();
        manager.TryRegister("a", CreateScope());
        manager.TryRegister("b", CreateScope());

        var list = manager.ListAll();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, i => i.AgentId == "a");
        Assert.Contains(list, i => i.AgentId == "b");
    }
}
