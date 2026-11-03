using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Fibonacci-zoned compaction strategy.
/// Groups blocks into Fibonacci-sized zones by turn, identifies already-compressed zones
/// (summary+turn markers from previous compactions), skips them, and only summarizes
/// uncompressed old zones. Zones 1-2 (the two newest turns) are always preserved intact.
/// </summary>
internal static class FiboStrategy
{
    /// <summary>
    /// Distribute a flat list of turn groups (no agent_data) into Fibonacci-sized zones,
    /// starting from the most recent turn backwards.
    /// Zone sizes: 1, 1, 2, 3, 5, 8, ... turns.
    /// </summary>
    internal static List<List<MemoryBlock>> DistributeTurnGroups(List<List<MemoryBlock>> turnList)
    {
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

    /// <summary>Execute the fibo compaction.</summary>
    internal static async Task<bool> CompactAsync(
        string agentId,
        CompressionOptions compOpts,
        List<MemoryBlock> blocks,
        string? model,
        IBlockStore blockStore,
        IChatClient chatClient,
        IAgentStore agentStore,
        ILogger logger,
        int contextWindow)
    {
        if (blocks.Count <= 1)
            return false;

        // The header is the anchor the rewritten history is spliced onto — ReplaceBlocksSinceAsync
        // cuts at its number + 1 — and the only block that survives untouched. Resolve it before any
        // summarization: without it there is nowhere to splice, and those LLM calls are paid for
        // nothing.
        var agentBlock = CompactionService.FindAgentDataBlock(blocks, agentId, logger);
        if (agentBlock is null)
            return false;

        // 1. Group ALL blocks into turns
        var turnGroups = CompactionService.GroupByTurns(blocks);
        if (turnGroups.Count <= 1)
            return false;

        // 2. Classify zones + apply hard mode checks
        var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);
        var zoneClass = CompactionService.ClassifyZones(turnGroups, threshold, logger);
        var safeGroups = zoneClass.SafeGroups;
        var compressedGroups = zoneClass.CompressedGroups;
        var toCompressGroups = zoneClass.ToCompressGroups;

        if (toCompressGroups.Count == 0)
            return false;

        // 3. Distribute to_compress groups into Fibonacci zones
        var zones = DistributeTurnGroups(toCompressGroups);
        if (zones.Count == 0)
            return false;

        logger.LogInformation("Compacting session {SessionId}: {TotalBlocks} blocks, {ZoneCount} old zones to summarize (fibo), threshold {Threshold}%",
            agentId, blocks.Count, zones.Count, compOpts.AutoThreshold);

        // Summarize all old zones via LLM in parallel
        var zoneTasks = new List<(List<MemoryBlock> zone, Task<(string? Summary, long Hit, long Miss, long Output, long Reasoning)> task)>();
        foreach (var zone in zones)
        {
            if (zone.Count == 0) continue;
            zoneTasks.Add((zone, CompactionService.SummarizeZoneAsync(agentId, zone, model, chatClient, agentStore, logger, structured: false)));
        }

        var summarizedFallback = new List<MemoryBlock>();
        var summaryResults = new List<(string Summary, long Hit, long Miss, long Output, long Reasoning)>();
        foreach (var (zone, task) in zoneTasks)
        {
            var (summary, hit, miss, output, reasoning) = await task;
            if (summary is not null)
                summaryResults.Add((summary, hit, miss, output, reasoning));
            else
                summarizedFallback.AddRange(zone);
        }

        if (summaryResults.Count == 0 && safeGroups.Count == 0 && summarizedFallback.Count == 0)
            return false;

        // 5. Build compacted history:
        //    agent_data (unchanged)
        //    + already compressed zones (pass through)
        //    + new summaries + turn markers (from to_compress zones)
        //    + safe zones (preserved)
        var nextNumber = agentBlock.Number + 1;
        var newBlocks = new List<MemoryBlock>();

        // Already compressed zones — pass through untouched
        foreach (var group in compressedGroups)
        {
            foreach (var block in group)
            {
                block.Number = nextNumber++;
                newBlocks.Add(block);
            }
        }

        // New summaries — each with its own turn marker
        foreach (var (summary, hit, miss, output, reasoning) in summaryResults)
        {
            var block = MemoryBlock.AgentMessage(summary, model: model);
            block.Number = nextNumber++;
            block.Compressed = true;
            newBlocks.Add(block);

            // The marker records two different things: the summarization call's real billed usage
            // (`hit`/`miss` are its prompt, `out_` its billed output — already including `reasoning`)
            // and the size of what was actually stored. A summary is pure result text, so the whole
            // stored size lands in `stored_text` and none of it in `stored_reasoning`.
            var stored = CompactionService.EstimateZoneTokens([block]);
            var turnBlock = MemoryBlock.TurnMarker(
                JsonSerializer.Serialize(new
                {
                    hit, miss, out_ = output, reasoning,
                    stored, stored_reasoning = 0L, stored_text = stored, stored_tool = 0L
                }));
            turnBlock.Number = nextNumber++;
            newBlocks.Add(turnBlock);
        }

        // Fallback (summarization failed — keep original blocks)
        foreach (var block in summarizedFallback)
        {
            block.Number = nextNumber++;
            newBlocks.Add(block);
        }

        // Safe zones (last 2 turns) — preserved intact
        foreach (var group in safeGroups)
        {
            foreach (var block in group)
            {
                block.Number = nextNumber++;
                newBlocks.Add(block);
            }
        }

        // Atomically replace history
        await blockStore.ReplaceBlocksSinceAsync(
            agentId,
            fromNumber: agentBlock.Number + 1,
            newBlocks,
            nextNumber);

        logger.LogInformation("Compacted session {SessionId}: {SummaryCount} summaries, {CompressedCount} compressed preserved, {FallbackCount} fallback blocks, {SafeCount} safe preserved (fibo)",
            agentId, summaryResults.Count, compressedGroups.Count, summarizedFallback.Count, safeGroups.Count);
        return true;
    }
}