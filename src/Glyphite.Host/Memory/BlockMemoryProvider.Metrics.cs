using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Memory;

public partial class BlockMemoryProvider
{
    private async Task<int> CountTokensAsync(List<MemoryBlock> blocks, string sessionId)
    {
        if (blocks.Count == 0) return 0;
        if (_encoding is null) return 0;

        var total = 0;
        foreach (var block in blocks)
            total += _encoding.Encode(block.ToContextString()).Count;
        return total;
    }

    public async Task<(int TotalBlocks, int TotalTokens, Dictionary<string, int> TypeStats)> ComputeStatsAsync(string sessionId)
    {
        var blocks = await _blockStore.LoadBlocksAsync(sessionId);
        if (blocks.Count == 0) return (0, 0, new());

        var typeStats = await _blockStore.GetBlockTypeStatsAsync(sessionId);
        return (blocks.Count, await CountTokensAsync(blocks, sessionId), typeStats);
    }

}
