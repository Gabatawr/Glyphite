using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Memory;

public partial class BlockMemoryProvider
{
    public async Task<(int TotalBlocks, int TotalTokens, Dictionary<string, int> TypeStats)> ComputeStatsAsync(string sessionId)
    {
        var blocks = await _blockStore.LoadBlocksAsync(sessionId);
        if (blocks.Count == 0) return (0, 0, new());

        var typeStats = await _blockStore.GetBlockTypeStatsAsync(sessionId);
        return (blocks.Count, 0, typeStats);
    }
}
