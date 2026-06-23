using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface IBlockMemoryProvider
{
    AsyncLocal<HashSet<string>?> CurrentExecutedIds { get; }

    Task<List<ChatMessage>> BuildContextAsync(string agentId, string? model = null, int? contextWindow = null);
    Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id);
    Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id, BlockType? type = null, int? limit = null, bool desc = true);
    Task<MemoryBlock?> GetBlockAsync(string id, double number, bool includeDeleted = false);
    Task UpdateBlockDataAsync(string agentId, double number, Dictionary<string, object>? data);
    Task<int> RemoveBlocksAsync(string agentId, Predicate<MemoryBlock> match);
    Task<string> DeleteBlocksAsync(string agentId, double[] numbers, bool cascade = true);
    Task<int> RecoverBlocksAsync(string agentId, double[] numbers, bool cascade = false);
    Task<string> DeleteBlocksByFilterAsync(string agentId, string[]? types, string? recent);
    Task<bool> AgentExistsAsync(string agentId);
    Task<bool> SetAgentModelAsync(string agentId, string model);
    Task<string?> GetAgentModelAsync(string agentId);
    Task<string?> GetModelAsync(string id);
    Task StoreErrorAsync(string agentId, string error);
    Task<(int TotalBlocks, int TotalTokens, Dictionary<string, int> TypeStats)> ComputeStatsAsync(string agentId);
    Task<(long Hit, long Miss, long Output)> GetUsageAsync(string agentId);
}