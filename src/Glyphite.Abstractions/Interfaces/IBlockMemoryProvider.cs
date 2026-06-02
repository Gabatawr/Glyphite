using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface IBlockMemoryProvider
{
    AsyncLocal<HashSet<string>?> CurrentExecutedIds { get; }
    string? AgentFilePath { get; set; }

    Task<List<ChatMessage>> BuildContextAsync(string sessionId, string? model = null, int? contextWindow = null);
    Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id);
    Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id, BlockType? type = null, int? limit = null, bool desc = true);
    Task<MemoryBlock?> GetBlockAsync(string id, double number, bool includeDeleted = false);
    Task UpdateBlockDataAsync(string sessionId, double number, Dictionary<string, object>? data);
    Task<int> RemoveBlocksAsync(string sessionId, Predicate<MemoryBlock> match);
    Task<string> DeleteBlocksAsync(string sessionId, double[] numbers);
    Task<int> RecoverBlocksAsync(string sessionId, double[] numbers);
    Task<string> DeleteBlocksByFilterAsync(string sessionId, string[]? types, string? recent);
    Task<bool> AgentExistsAsync(string sessionId);
    Task<bool> SetAgentModelAsync(string sessionId, string model);
    Task<string?> GetAgentModelAsync(string sessionId);
    Task<string?> GetModelAsync(string id);
    Task StoreErrorAsync(string sessionId, string error);
    Task<(int TotalBlocks, int TotalTokens, Dictionary<string, int> TypeStats)> ComputeStatsAsync(string sessionId);
}
