using Glyphite.Abstractions.Models;

namespace Glyphite.Abstractions.Interfaces;

public interface IAgentStore
{
    Task EnsureSessionAsync(string id, string? homePath = null);
    Task<string?> GetAgentHomePathAsync(string id);
    Task SetAgentHomePathAsync(string id, string homePath);
    Task<string?> GetAgentCreatedAtAsync(string id);
    Task<string?> GetAgentModelAsync(string id);
    Task<bool> SetAgentModelAsync(string id, string model);
    Task<bool> AgentExistsAsync(string id);
    /// <summary>Current value of the session's block counter (read-only observation — block numbers
    /// are only ever allocated by <c>IBlockStore.AppendBlocksAsync</c>).</summary>
    Task<double> GetNextNumberAsync(string id);
    Task DeleteSessionAsync(string agentId);
    Task RecordLaunchAsync(string agentId, string path);
    Task<List<(string path, string lastActive)>> GetLaunchesAsync(string agentId);
    Task<string?> GetLastLaunchPathAsync(string agentId);
    Task<string?> GetLastActiveAgentAsync(string cwd);
    Task ForkSessionAsync(string sourceId, string targetId, string cwd);
    Task<List<string>> ListAgentsAsync();
    Task<string?> GetSessionIdByWorkingDirectoryAsync(string cwd);

    // Usage tracking. `output` is the billed completion count and already includes `reasoning`
    // (hidden thinking) — reasoning is stored only to explain the bill, never added on top.
    Task RecordUsageAsync(string agentId, long cacheHit, long cacheMiss, long output, long reasoning = 0, long lastRequestHit = 0, long lastRequestMiss = 0, string? model = null);
    Task<(long Hit, long Miss, long Output, long Reasoning)> GetUsageAsync(string agentId);
    Task<List<(string Model, long Hit, long Miss, long Output, long Reasoning)>> GetUsageByModelAsync(string agentId);
    Task<(long Hit, long Miss, long Output, long LastHit, long LastMiss)> GetLastUsageAsync(string agentId);
    Task ClearUsageAsync(string agentId);

    // Pending run tracking (crash-safe: survives process kill)
    Task SetPendingRunAsync(string agentId, string mode, double? blockCheckpoint = null);
    Task ClearPendingRunAsync(string agentId);
    Task<List<(string AgentId, string Mode, double? BlockCheckpoint)>> GetPendingRunsAsync();
}