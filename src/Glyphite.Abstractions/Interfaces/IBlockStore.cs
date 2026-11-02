using Glyphite.Abstractions.Models;

namespace Glyphite.Abstractions.Interfaces;

public interface IBlockStore
{
    Task<List<MemoryBlock>> LoadBlocksAsync(string agentId);
    Task<MemoryBlock?> GetBlockAsync(string agentId, double number, bool includeDeleted = false);
    Task<List<MemoryBlock>> LoadBlocksByTypeAsync(string agentId, BlockType? type, int? limit, bool desc);
    /// <summary>
    /// Append blocks, assigning each one its number from the session counter.
    /// This is the only place a block number is handed out: allocation and insert are atomic, so two
    /// writers (a turn and a tool) can never receive the same number. The counter never goes below an
    /// existing block's number, so a stale counter heals instead of colliding.
    /// </summary>
    Task AppendBlocksAsync(string agentId, List<MemoryBlock> newBlocks);
    Task UpdateBlockAsync(string agentId, double number, string? content = null, Dictionary<string, object>? data = null, string? model = null);
    Task UpdateBlockDataAsync(string agentId, double number, Dictionary<string, object>? data);
    Task UpdateBlockToolResultAsync(string agentId, double number, string? toolResult);
    Task UpdateBlockContentAsync(string agentId, double number, string content);
    Task<int> RemovePeekBlocksAsync(string agentId, bool includeReasoning = true);
    Task<int> ClearPeekMarkersAsync(string agentId, bool includeReasoning = true);
    Task<Dictionary<string, int>> GetPeekBlockStatsAsync(string agentId, bool includeReasoning = true);
    Task<int> RemoveBlocksAsync(string agentId, Predicate<MemoryBlock> match);
    Task ClearAgentBlocksAsync(string agentId);
    Task DeleteBlocksSinceAsync(string agentId, double fromNumber);
    Task ReplaceBlocksSinceAsync(string agentId, double fromNumber, List<MemoryBlock> newBlocks, double nextNumber);
    Task<int> GetBlockCountAsync(string agentId);
    Task<Dictionary<string, int>> GetBlockTypeStatsAsync(string agentId);
}
