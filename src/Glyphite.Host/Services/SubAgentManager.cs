using System.Collections.Concurrent;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.DI;

namespace Glyphite.Host.Services;

/// <summary>Tracks subagent scopes. Singleton.</summary>
public sealed class SubAgentManager
{
    private readonly ConcurrentDictionary<string, AgentScopeEntry> _entries = new();

    // ── Scope management ──

    public bool TryRegister(string agentId, AgentScope scope) =>
        _entries.TryAdd(agentId, new AgentScopeEntry(scope));

    public AgentScope? GetScope(string agentId) =>
        _entries.TryGetValue(agentId, out var entry) ? entry.Scope : null;

    public bool Exists(string agentId) => _entries.ContainsKey(agentId);

    public void Remove(string agentId)
    {
        if (_entries.TryRemove(agentId, out var entry))
        {
            entry.Scope.Dispose();
            entry.Semaphore.Dispose();
        }
    }

    /// <summary>Execute a task on a subagent and return the result.</summary>
    public async Task<string> RunAsync(string agentId, Func<AgentScope, Task<string>> runFunc)
    {
        if (!_entries.TryGetValue(agentId, out var entry))
            throw new InvalidOperationException($"Subagent '{agentId}' not registered.");

        await entry.Semaphore.WaitAsync();
        try
        {
            return await Task.Run(async () => await runFunc(entry.Scope));
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    // ── Results ──

    public string? GetResult(string agentId) =>
        _entries.TryGetValue(agentId, out var entry) ? entry.StoredResult : null;

    public List<SubAgentInfo> ListAll() =>
        _entries.Select(kv => new SubAgentInfo(kv.Key, kv.Value.CreatedAt)).ToList();

    // ── Types ──

    public sealed record SubAgentInfo(string AgentId, DateTime CreatedAt);

    private sealed class AgentScopeEntry(AgentScope scope)
    {
        public AgentScope Scope { get; } = scope;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public string? StoredResult { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
    }
}
