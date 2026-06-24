using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Fibonacci-zoned compaction strategy.
/// Groups blocks into Fibonacci-sized zones by turn, strips unprotected blocks from old zones
/// (zone 3+), summarizes them via LLM, and replaces history with summaries + intact new zones.
/// Zones 1-2 (the two newest turns) are preserved entirely intact.
/// </summary>
internal static class FiboPartsStrategy
{
    /// <summary>
    /// Distribute turn groups into Fibonacci-sized zones, starting from the most recent turn backwards.
    /// Zone sizes: 1, 1, 2, 3, 5, 8, ... turns (the first zone after agent_data).
    /// The agent_data group is never included in any zone.
    /// </summary>
    internal static List<List<MemoryBlock>> DistributeFibonacci(List<List<MemoryBlock>> turnGroups)
    {
        // turnGroups[0] = agent_data group (oldest)
        // turnGroups[1..] = turn groups (oldest first)
        // We reverse to distribute from newest backwards, then reverse back.

        var turnList = turnGroups.Skip(1).ToList(); // Skip agent_data group
        if (turnList.Count == 0)
            return [];

        turnList.Reverse(); // Newest first

        var zones = new List<List<MemoryBlock>>();
        var fibPrev = 0;
        var fibCurr = 1;
        var index = 0;

        while (index < turnList.Count)
        {
            var zoneSize = fibCurr;
            var zone = new List<MemoryBlock>();

            for (var taken = 0; taken < zoneSize && index < turnList.Count; taken++, index++)
            {
                zone.AddRange(turnList[index]);
            }

            if (zone.Count > 0)
            {
                zones.Add(zone);
            }

            var next = fibPrev + fibCurr;
            fibPrev = fibCurr;
            fibCurr = next;
        }

        // zones are newest-first, reverse to be oldest-first
        zones.Reverse();
        return zones;
    }

    /// <summary>Execute the fibo-parts compaction.</summary>
    internal static async Task<bool> CompactAsync(
        string agentId,
        CompressionOptions compOpts,
        MemoryOptions memOpts,
        List<MemoryBlock> blocks,
        string? model,
        IBlockStore blockStore,
        IChatClient chatClient,
        IAgentStore agentStore,
        ILogger logger)
    {
        if (blocks.Count <= 1)
            return false;

        // 1. Group ALL blocks into turns
        var turnGroups = CompactionService.GroupByTurns(blocks);
        if (turnGroups.Count <= 1)
            return false;

        // 2. Distribute turn groups into Fibonacci zones
        var zones = DistributeFibonacci(turnGroups);
        if (zones.Count <= 2)
            return false;

        var protectedTypes = new HashSet<BlockType>(
            memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));

        var isSubagentTool = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "subagent_run", "subagent_use" };

        var summarizeCount = zones.Count - 2;

        var allUnprotectedNums = new HashSet<double>();
        var zoneProtectedBlocks = new List<List<MemoryBlock>>();

        // Also clean non-agent_data blocks from the agent_data group (turn 0)
        // These are blocks that ended up in the same group as agent_data (before first turn marker)
        // and would otherwise survive compaction forever
        var agentDataGroup = turnGroups[0];
        foreach (var b in agentDataGroup)
        {
            if (b.Type != BlockType.agent_data)
                allUnprotectedNums.Add(b.Number);
        }

        for (var i = 0; i < summarizeCount; i++)
        {
            var zone = zones[i];
            var keepInZone = new List<MemoryBlock>();

            foreach (var b in zone)
            {
                if (protectedTypes.Contains(b.Type) ||
                    (b.Type == BlockType.tool && b.ToolName is not null && isSubagentTool.Contains(b.ToolName)))
                    keepInZone.Add(b);
                else
                    allUnprotectedNums.Add(b.Number);
            }

            zoneProtectedBlocks.Add(keepInZone);
        }

        var totalBlocks = blocks.Count;
        logger.LogInformation("Compacting session {SessionId}: {TotalBlocks} blocks, {ZoneCount} old zones to summarize (fibo-parts), threshold {Threshold}%",
            agentId, totalBlocks, summarizeCount, compOpts.AutoThreshold);

        // Summarize all old zones via LLM in parallel
        var zoneTasks = new List<(List<MemoryBlock> zone, Task<string?> task)>();
        foreach (var zone in zoneProtectedBlocks)
        {
            if (zone.Count == 0) continue;
            zoneTasks.Add((zone, CompactionService.SummarizeZoneAsync(agentId, zone, model, chatClient, agentStore, logger, structured: false)));
        }

        var summaries = new List<string>();
        var summarizedFallback = new List<MemoryBlock>();
        foreach (var (zone, task) in zoneTasks)
        {
            var summary = await task;
            if (summary is not null)
                summaries.Add(summary);
            else
                summarizedFallback.AddRange(zone);
        }

        // Collect preserved blocks from the two newest zones
        var preserved = new List<MemoryBlock>();
        for (var i = summarizeCount; i < zones.Count; i++)
            preserved.AddRange(zones[i]);

        if (summaries.Count == 0 && preserved.Count == 0 && summarizedFallback.Count == 0)
            return false;

        // Find agent_data block
        var agentBlock = blocks.FirstOrDefault(b => b.Type == BlockType.agent_data);
        if (agentBlock is null)
        {
            logger.LogWarning("Compaction failed for session {SessionId}: agent_data block not found", agentId);
            return false;
        }

        // Build compacted history: summaries + fallback + preserved
        var nextNumber = agentBlock.Number + 1;
        var newBlocks = new List<MemoryBlock>();

        foreach (var summary in summaries)
        {
            var block = MemoryBlock.AgentMessage(summary, model: model);
            block.Number = nextNumber++;
            newBlocks.Add(block);
        }

        foreach (var block in summarizedFallback)
        {
            block.Number = nextNumber++;
            newBlocks.Add(block);
        }

        foreach (var block in preserved)
        {
            block.Number = nextNumber++;
            newBlocks.Add(block);
        }

        // Atomically replace history
        await blockStore.ReplaceBlocksSinceAsync(
            agentId,
            fromNumber: agentBlock.Number + 1,
            newBlocks,
            nextNumber,
            softDeleteNums: allUnprotectedNums.Count > 0 ? allUnprotectedNums : null);

        logger.LogInformation("Compacted session {SessionId}: {SummaryCount} summaries, {FallbackCount} fallback blocks, {PreservedCount} preserved, {SoftDeletedCount} soft-deleted (fibo-parts)",
            agentId, summaries.Count, summarizedFallback.Count, preserved.Count, allUnprotectedNums.Count);
        return true;
    }
}
