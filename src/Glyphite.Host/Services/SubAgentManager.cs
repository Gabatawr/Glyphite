using System.Collections.Concurrent;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.DI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>Tracks subagent scopes. Singleton.</summary>
public sealed class SubAgentManager
{
    private readonly ConcurrentDictionary<string, AgentScopeEntry> _entries = new();
    private readonly ConcurrentDictionary<string, bool> _ephemeralFlags = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public SubAgentManager(ILogger<SubAgentManager>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentManager>.Instance;
    }

    /// <summary>Mark an agent as ephemeral (temp agent from subagent_run).
    /// Ephemeral agents' config changes should NOT be persisted to DB.</summary>
    public void SetEphemeral(string agentId, bool ephemeral = true)
    {
        if (ephemeral)
            _ephemeralFlags[agentId] = true;
        else
            _ephemeralFlags.TryRemove(agentId, out _);
        _logger.LogDebug("Agent '{AgentId}' ephemeral={Ephemeral}", agentId, ephemeral);
    }

    /// <summary>Check if an agent is marked ephemeral (temp / run-mode subagent).</summary>
    public bool IsEphemeral(string agentId) =>
        _ephemeralFlags.TryGetValue(agentId, out var eph) && eph;

    // ── Scope management ──

    public bool TryRegister(string agentId, AgentScope scope)
    {
        if (_entries.TryAdd(agentId, new AgentScopeEntry(scope)))
        {
            _logger.LogDebug("Registered scope for subagent '{AgentId}'", agentId);
            return true;
        }
        _logger.LogWarning("Failed to register scope for subagent '{AgentId}' — already exists", agentId);
        return false;
    }

    public AgentScope? GetScope(string agentId) =>
        _entries.TryGetValue(agentId, out var entry) ? entry.Scope : null;

    public bool Exists(string agentId) => _entries.ContainsKey(agentId);

    public void Remove(string agentId)
    {
        if (_entries.TryRemove(agentId, out var entry))
        {
            entry.Scope.Dispose();
            entry.Semaphore.Dispose();
            _ephemeralFlags.TryRemove(agentId, out _);
            _logger.LogDebug("Removed scope for subagent '{AgentId}'", agentId);
        }
    }

    /// <summary>Execute a task on a subagent and return the result.</summary>
    public async Task<string> RunAsync(string agentId, Func<AgentScope, Task<string>> runFunc)
    {
        if (!_entries.TryGetValue(agentId, out var entry))
        {
            _logger.LogError("Subagent '{AgentId}' not registered — cannot run task", agentId);
            throw new InvalidOperationException($"Subagent '{agentId}' not registered.");
        }

        await entry.Semaphore.WaitAsync();
        try
        {
            _logger.LogDebug("Running task on subagent '{AgentId}'", agentId);
            return await runFunc(entry.Scope);
        }
        finally
        {
            entry.Semaphore.Release();
            _logger.LogDebug("Subagent '{AgentId}' task completed", agentId);
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
