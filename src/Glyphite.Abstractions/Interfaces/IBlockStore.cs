using Glyphite.Abstractions.Models;

namespace Glyphite.Abstractions.Interfaces;

public interface IBlockStore
{
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
    Task<(int Removed, List<double> Protected)> DeleteBlocksAsync(string agentId, double[] numbers, HashSet<BlockType>? protectedTypes = null);
    Task<int> DeleteBlocksByFilterAsync(string agentId, string[]? types, TimeSpan? recent, HashSet<BlockType>? protectedTypes = null);
    Task ClearAgentBlocksAsync(string agentId);
    Task DeleteBlocksSinceAsync(string agentId, double fromNumber);
    Task ReplaceBlocksSinceAsync(string agentId, double fromNumber, List<MemoryBlock> newBlocks, double nextNumber, HashSet<double>? softDeleteNums = null);
    Task<int> GetBlockCountAsync(string agentId);
    Task<Dictionary<string, int>> GetBlockTypeStatsAsync(string agentId);
}
