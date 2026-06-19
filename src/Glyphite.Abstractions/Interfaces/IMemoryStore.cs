using Glyphite.Abstractions.Models;

namespace Glyphite.Abstractions.Interfaces;

public interface IMemoryStore : IDisposable
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

    Task<List<MemoryBlock>> LoadBlocksAsync(string agentId);
    Task<MemoryBlock?> GetBlockAsync(string agentId, double number, bool includeDeleted = false);
    Task<List<MemoryBlock>> LoadBlocksByTypeAsync(string agentId, BlockType? type, int? limit, bool desc);
    Task AppendBlocksAsync(string agentId, List<MemoryBlock> newBlocks, double nextNumber);
    Task UpdateBlockAsync(string agentId, double number, string? content = null, Dictionary<string, object>? data = null, string? model = null);
    Task UpdateBlockDataAsync(string agentId, double number, Dictionary<string, object>? data);
    Task UpdateBlockToolResultAsync(string agentId, double number, string? toolResult);
    Task UpdateBlockContentAsync(string agentId, double number, string content);
    Task<int> RemovePeekBlocksAsync(string agentId, bool includeReasoning = true);
    Task<int> ClearPeekMarkersAsync(string agentId, bool includeReasoning = true);
    Task<Dictionary<string, int>> GetPeekBlockStatsAsync(string agentId, bool includeReasoning = true);
    Task<int> RemoveBlocksAsync(string agentId, Predicate<MemoryBlock> match);
    Task<(int Removed, List<double> Protected)> DeleteBlocksAsync(string agentId, double[] numbers, HashSet<BlockType>? protectedTypes = null, bool cascade = true);
    Task<int> RecoverBlocksAsync(string agentId, double[] numbers, bool cascade = false);
    Task<int> DeleteBlocksByFilterAsync(string agentId, string[]? types, TimeSpan? recent, HashSet<BlockType>? protectedTypes = null);
    Task ForkSessionAsync(string sourceId, string targetId, string cwd);
    Task ClearAgentBlocksAsync(string agentId);
    Task<List<string>> ListAgentsAsync();
    Task<int> GetBlockCountAsync(string agentId);
    Task<Dictionary<string, int>> GetBlockTypeStatsAsync(string agentId);
    // Config
    Task<string?> GetConfigAsync(string key, string scope = "global", string? agentId = null);
    Task UpsertConfigAsync(string key, string value, string scope = "global", string? agentId = null);
    Task DeleteConfigAsync(string key, string scope = "global", string? agentId = null);
    Task<Dictionary<string, string>> GetMergedConfigAsync(string? agentId = null);
    Task<string?> GetSessionIdByWorkingDirectoryAsync(string cwd);

    // Usage tracking
    Task RecordUsageAsync(string agentId, long cacheHit, long cacheMiss, long output, long lastRequestHit = 0, long lastRequestMiss = 0, string? model = null);
    Task<(long Hit, long Miss, long Output)> GetUsageAsync(string agentId);
    Task<List<(string Model, long Hit, long Miss, long Output)>> GetUsageByModelAsync(string agentId);
    Task<(long Hit, long Miss, long Output, long LastHit, long LastMiss)> GetLastUsageAsync(string agentId);
    Task ClearUsageAsync(string agentId);
}
