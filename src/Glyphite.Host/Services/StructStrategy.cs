using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Structured compaction strategy.
///
/// Sends ALL content from agent_data (excluding it) through the last
/// non-safe turn group (zones 3+, including already compressed zones)
/// into a single LLM call with a structured template
/// (## Goal, ## Progress, ## Key Decisions, ## Relevant Files, ## Next Steps).
/// The LLM sees the full conversation for the most informed summary.
///
/// After summarisation:
///   - Old turns (3+) have their unprotected blocks soft-deleted.
///   - Already compressed zones (summary+turn from previous compactions)
///     pass through untouched — their blocks are never soft-deleted.
///   - Non-agent_data blocks in group 0 (before first turn) are also soft-deleted.
///   - The last 2 turns (zones 1 &amp; 2) are preserved intact for granular access.
///
/// Block order in DB/context:
///   agent_data → compressed zones (pass-through) → safe zones (preserved)
///   → struct summary (covers everything) → turn marker
///
/// Compressed zones sit first (oldest compressed history), then safe zones
/// provide increasingly granular recent detail, then the summary covers
/// everything as a high-level reference at the end.
/// </summary>
internal static class StructStrategy
{
    /// <summary>Execute the struct compaction.</summary>
    internal static async Task<bool> CompactAsync(
        string agentId,
        CompressionOptions compOpts,
        MemoryOptions memOpts,
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

        var turnGroups = CompactionService.GroupByTurns(blocks);
        if (turnGroups.Count <= 3)
            return false; // need agent_data + at least 3 turn groups to save 2 and summarize 1+

        var protectedTypes = new HashSet<BlockType>(
            memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));

        var isSubagentTool = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "subagent_run", "subagent_use" };

        // Classify turn groups (skip index 0 = agent_data group)
        // Walk from newest (end) backwards:
        //   - Last 2 = safe (preserved intact)
        //   - Groups with Compressed=true = already compressed (pass through)
        //   - Everything else = to_compress (will be fully summarized)
        var safeGroups = new List<List<MemoryBlock>>();
        var compressedGroups = new List<List<MemoryBlock>>();
        var toCompressGroups = new List<List<MemoryBlock>>();

        for (var i = turnGroups.Count - 1; i >= 1; i--)
        {
            var group = turnGroups[i];
            var rankFromNewest = turnGroups.Count - 1 - i;

            if (rankFromNewest < 2)
            {
                safeGroups.Add(group);
            }
            else if (group.Any(b => b.Compressed))
            {
                compressedGroups.Add(group);
            }
            else
            {
                toCompressGroups.Add(group);
            }
        }

        safeGroups.Reverse();
        compressedGroups.Reverse();
        toCompressGroups.Reverse();

        // Hard mode: if compressed zones consume >= 2/3 of threshold, recompress them too
        var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);
        var compressedTokens = FiboStrategy.SumCompressedOutput(compressedGroups);
        if (compressedTokens >= (int)(2.0 / 3.0 * threshold) && compressedGroups.Count > 0)
        {
            logger.LogInformation("Hard mode: {CompressedTokens} compressed tokens >= 2/3 of threshold {Threshold}, recompressing {Count} compressed zones",
                compressedTokens, threshold, compressedGroups.Count);
            toCompressGroups.InsertRange(0, compressedGroups);
            compressedGroups.Clear();
        }

        if (toCompressGroups.Count == 0)
            return false;

        // ── Build LLM input: EVERYTHING except agent_data ──
        // Send ALL blocks — group 0 (excl. agent_data), compressed, to_compress, and safe.
        // The LLM sees the complete picture for the most comprehensive summary.
        // Safe zones are preserved intact after compaction, but included in LLM input
        // so the summary covers the entire session.
        var summarizeBlocks = new List<MemoryBlock>();

        // Group 0: blocks before first turn marker (excluding agent_data itself)
        foreach (var b in turnGroups[0])
        {
            if (b.Type != BlockType.agent_data)
                summarizeBlocks.Add(b);
        }

        // All turn groups: compressed + to_compress + safe
        foreach (var group in compressedGroups)
        {
            foreach (var b in group)
                summarizeBlocks.Add(b);
        }

        foreach (var group in toCompressGroups)
        {
            foreach (var b in group)
                summarizeBlocks.Add(b);
        }

        foreach (var group in safeGroups)
        {
            foreach (var b in group)
                summarizeBlocks.Add(b);
        }

        // ── Build soft-delete set: unprotected blocks from to_compress + group 0 junk ──
        // Compressed groups and safe groups are NEVER soft-deleted.
        var allOldNums = new HashSet<double>();

        // Group 0: non-agent_data, non-compressed blocks → soft-delete
        foreach (var b in turnGroups[0])
        {
            if (b.Type != BlockType.agent_data && !b.Compressed)
                allOldNums.Add(b.Number);
        }

        // To-compress zones: unprotected blocks → soft-delete
        foreach (var group in toCompressGroups)
        {
            foreach (var b in group)
            {
                if (!protectedTypes.Contains(b.Type) &&
                    !(b.Type == BlockType.tool && b.ToolName is not null && isSubagentTool.Contains(b.ToolName)))
                {
                    allOldNums.Add(b.Number);
                }
            }
        }

        if (summarizeBlocks.Count == 0)
            return false;

        logger.LogInformation("Compacting session {SessionId}: {TotalBlocks} blocks, {AllBlocksToSummarize} blocks to summarize (struct, unfiltered), threshold {Threshold}%",
            agentId, blocks.Count, summarizeBlocks.Count, compOpts.AutoThreshold);

        // ── Single LLM summarization with structured template ──
        var (summary, _, _, summaryOutput) = await CompactionService.SummarizeZoneAsync(
            agentId, summarizeBlocks, model, chatClient, agentStore, logger, structured: true);

        if (summary is null && safeGroups.Count == 0)
            return false;

        // Find agent_data block
        var agentBlock = blocks.FirstOrDefault(b => b.Type == BlockType.agent_data);
        if (agentBlock is null)
        {
            logger.LogWarning("Compaction failed for session {SessionId}: agent_data block not found", agentId);
            return false;
        }

        // ── Build output: compressed zones FIRST (pass through), then safe (preserved), then summary ──
        var nextNumber = agentBlock.Number + 1;
        var newBlocks = new List<MemoryBlock>();

        // Already compressed zones — pass through untouched (oldest history, then granular detail)
        foreach (var group in compressedGroups)
        {
            foreach (var block in group)
            {
                block.Number = nextNumber++;
                newBlocks.Add(block);
            }
        }

        // Safe zones — preserved intact (most recent granular context)
        foreach (var group in safeGroups)
        {
            foreach (var block in group)
            {
                block.Number = nextNumber++;
                newBlocks.Add(block);
            }
        }

        // Summary last — covers everything, sits at end as high-level reference
        if (summary is not null)
        {
            var block = MemoryBlock.AgentMessage(summary, model: model);
            block.Number = nextNumber++;
            block.Compressed = true;
            newBlocks.Add(block);

            // Turn marker with usage from summarization LLM call (hit=0, miss=output, out=output)
            var turnBlock = MemoryBlock.TurnMarker(
                JsonSerializer.Serialize(new { hit = 0L, miss = summaryOutput, out_ = summaryOutput }));
            turnBlock.Number = nextNumber++;
            newBlocks.Add(turnBlock);
        }

        // Atomically replace history: hard-delete everything after agent_data,
        // soft-delete old unprotected blocks, insert new order
        await blockStore.ReplaceBlocksSinceAsync(
            agentId,
            fromNumber: agentBlock.Number + 1,
            newBlocks,
            nextNumber,
            softDeleteNums: allOldNums.Count > 0 ? allOldNums : null);

        logger.LogInformation("Compacted session {SessionId}: struct summary (first), {CompressedCount} compressed preserved, {SafeCount} safe preserved, {SoftDeletedCount} soft-deleted",
            agentId, compressedGroups.Count, safeGroups.Count, allOldNums.Count);
        return true;
    }
}
