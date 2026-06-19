using System.Collections.Concurrent;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.DI;

namespace Glyphite.Host.Services;

/// <summary>Tracks subagent scopes, pending parallel tasks, and results. Singleton.</summary>
public sealed class SubAgentManager
{
    private readonly ConcurrentDictionary<string, AgentScopeEntry> _entries = new();
    private readonly ConcurrentQueue<PendingTask> _parallelQueue = new();

    // ── Scope management ──

    public bool TryRegister(string agentId, AgentScope scope) =>
        _entries.TryAdd(agentId, new AgentScopeEntry(scope));

    public AgentScope? GetScope(string agentId) =>
        _entries.TryGetValue(agentId, out var entry) ? entry.Scope : null;

    public bool Exists(string agentId) => _entries.ContainsKey(agentId);

    public void Remove(string agentId)
    {
        if (_entries.TryRemove(agentId, out var entry))
            entry.Scope.Dispose();
    }

    /// <summary>Execute a task on a subagent and return the Task to await.</summary>
    public Task<string> RunAsync(string agentId, Func<AgentScope, Task<string>> runFunc)
    {
        if (!_entries.TryGetValue(agentId, out var entry))
            throw new InvalidOperationException($"Subagent '{agentId}' not registered.");

        var task = Task.Run(async () => await runFunc(entry.Scope));
        return task;
    }

    // ── Parallel queue ──

    public bool HasPendingParallel => !_parallelQueue.IsEmpty;

    /// <summary>Enqueue a parallel task. Throws if agentId already queued (duplicate check).</summary>
    public void EnqueueParallel(string agentId, string task, Func<AgentScope, Task<string>> runFunc)
    {
        // Check for duplicates in the queue
        if (_parallelQueue.Any(p => p.AgentId == agentId))
            throw new InvalidOperationException(
                $"Agent '{agentId}' appears twice in parallel group. " +
                "Use sequential mode to run multiple tasks on the same agent.");

        if (!_entries.TryGetValue(agentId, out var entry))
            throw new InvalidOperationException($"Subagent '{agentId}' not registered.");

        _parallelQueue.Enqueue(new PendingTask(agentId, task, runFunc, entry.Scope));
    }

    /// <summary>Flush all queued parallel tasks via Task.WhenAll. Returns (agentId, result, error) pairs.</summary>
    public async Task<List<(string AgentId, string? Result, string? Error)>> FlushParallelAsync()
    {
        if (_parallelQueue.IsEmpty) return [];

        var items = new List<PendingTask>();
        while (_parallelQueue.TryDequeue(out var item))
            items.Add(item);

        var runningTasks = items.Select(async item =>
        {
            try
            {
                var result = await Task.Run(async () => await item.RunFunc(item.Scope));
                return (item.AgentId, Result: result, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (item.AgentId, Result: (string?)null, Error: ex.Message);
            }
        }).ToList();

        return (await Task.WhenAll(runningTasks)).ToList();
    }

    // ── Results ──

    public string? GetResult(string agentId) =>
        _entries.TryGetValue(agentId, out var entry) ? entry.StoredResult : null;

    public List<SubAgentInfo> ListAll() =>
        _entries.Select(kv => new SubAgentInfo(kv.Key, kv.Value.CreatedAt)).ToList();

    // ── Types ──

    public sealed record SubAgentInfo(string AgentId, DateTime CreatedAt);

    private sealed record PendingTask(string AgentId, string Task, Func<AgentScope, Task<string>> RunFunc, AgentScope Scope);

    private sealed class AgentScopeEntry(AgentScope scope)
    {
        public AgentScope Scope { get; } = scope;
        public string? StoredResult { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
    }
}
