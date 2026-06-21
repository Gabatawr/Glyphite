using Glyphite.Abstractions.Models;

namespace Glyphite.Abstractions.Interfaces;

public interface IAgentStore
{
    Task EnsureSessionAsync(string id, string? homePath = null);
    Task<string?> GetAgentHomePathAsync(string id);
    Task<string?> GetAgentCreatedAtAsync(string id);
    Task<string?> GetAgentModelAsync(string id);
    Task<bool> SetAgentModelAsync(string id, string model);
    Task<bool> AgentExistsAsync(string id);
    Task<double> GetNextNumberAsync(string id);
    Task SetNextNumberAsync(string id, double next);
    Task DeleteSessionAsync(string agentId);
    Task RecordLaunchAsync(string agentId, string path);
    Task<List<(string path, string lastActive)>> GetLaunchesAsync(string agentId);
    Task<string?> GetLastLaunchPathAsync(string agentId);
    Task<string?> GetLastActiveAgentAsync(string cwd);
    Task ForkSessionAsync(string sourceId, string targetId, string cwd);
    Task<List<string>> ListAgentsAsync();
    Task<string?> GetSessionIdByWorkingDirectoryAsync(string cwd);

    // Usage tracking
    Task RecordUsageAsync(string agentId, long cacheHit, long cacheMiss, long output, long lastRequestHit = 0, long lastRequestMiss = 0, string? model = null);
    Task<(long Hit, long Miss, long Output)> GetUsageAsync(string agentId);
    Task<List<(string Model, long Hit, long Miss, long Output)>> GetUsageByModelAsync(string agentId);
    Task<(long Hit, long Miss, long Output, long LastHit, long LastMiss)> GetLastUsageAsync(string agentId);
    Task ClearUsageAsync(string agentId);
}
