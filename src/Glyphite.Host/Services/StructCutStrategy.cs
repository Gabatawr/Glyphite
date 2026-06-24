using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Structured-cut compaction strategy.
///
/// Sends ALL content from agent_data (excluding it) to the very end — every turn,
/// every tool result, every reasoning block — into a single LLM call with a
/// structured template (## Goal, ## Progress, ## Key Decisions, ## Relevant Files,
/// ## Next Steps). Because the LLM sees the full conversation without filtering,
/// the summary captures the entire session.
///
/// After summarisation:
///   - Old turns (3+) have their unprotected blocks soft-deleted.
///   - Non-agent_data blocks in group 0 (before first turn) are also soft-deleted.
///   - The last 2 turns (zones 1 &amp; 2) are preserved intact for granular access.
///
/// Block order in DB/context:
///   agent_data → preserved zone 2 (predposlednij) → preserved zone 1 (poslednij)
///   → struct-cut summary (last block, covers everything)
///
/// The summary sits last because it was generated with full context including the
/// preserved zones — it is a high-level reference for old history, not a preface.
/// </summary>
internal static class StructCutStrategy
{
    /// <summary>Execute the struct-cut compaction.</summary>
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
        // --- same guard as fibo-parts ---
        if (blocks.Count <= 1)
            return false;

        var turnGroups = CompactionService.GroupByTurns(blocks);
        if (turnGroups.Count <= 3)
            return false; // need agent_data + at least 3 turn groups to save 2 and summarize 1+

        var protectedTypes = new HashSet<BlockType>(
            memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));

        var isSubagentTool = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "subagent_run", "subagent_use" };

        // turnGroups[0]    = agent_data group (may include blocks before first turn marker)
        // turnGroups[1..^2] = old turn groups (3+) — will have unprotected blocks soft-deleted
        // turnGroups[^2..]  = last 2 turn groups (zones 1 & 2) — preserved intact

        var oldGroups = turnGroups.Skip(1).Take(turnGroups.Count - 3).ToList();
        var preserveGroups = turnGroups.TakeLast(2).ToList();

        if (oldGroups.Count == 0)
            return false;

        // ── Build LLM input: ALL blocks (unfiltered) ──
        // We send everything from agent_data (excluding the block itself) to the end,
        // so the LLM sees the complete picture for the best structured summary.
        var summarizeBlocks = new List<MemoryBlock>();

        // Group 0: blocks before first turn marker (excluding agent_data itself)
        foreach (var b in turnGroups[0])
        {
            if (b.Type != BlockType.agent_data)
                summarizeBlocks.Add(b);
        }

        // All turn groups: old (3+) and preserved (1, 2) — everything
        for (var i = 1; i < turnGroups.Count; i++)
        {
            foreach (var b in turnGroups[i])
                summarizeBlocks.Add(b);
        }

        // ── Build soft-delete set: unprotected blocks from old zones (3+) + group 0 junk ──
        var allOldNums = new HashSet<double>();

        // Group 0: non-agent_data blocks → soft-delete (they survive compaction otherwise)
        foreach (var b in turnGroups[0])
        {
            if (b.Type != BlockType.agent_data)
                allOldNums.Add(b.Number);
        }

        // Old zones (3+): unprotected blocks → soft-delete
        foreach (var group in oldGroups)
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

        var totalBlocks = blocks.Count;
        logger.LogInformation("Compacting session {SessionId}: {TotalBlocks} blocks, {AllBlocksToSummarize} blocks to summarize (struct-cut, unfiltered), threshold {Threshold}%",
            agentId, totalBlocks, summarizeBlocks.Count, compOpts.AutoThreshold);

        // ── Single LLM summarization with structured template ──
        var summary = await CompactionService.SummarizeZoneAsync(
            agentId, summarizeBlocks, model, chatClient, agentStore, logger, structured: true);

        // Preserved blocks from the two newest turn groups (intact)
        var preserved = new List<MemoryBlock>();
        foreach (var group in preserveGroups)
            preserved.AddRange(group);

        if (summary is null && preserved.Count == 0)
            return false;

        // Find agent_data block
        var agentBlock = blocks.FirstOrDefault(b => b.Type == BlockType.agent_data);
        if (agentBlock is null)
        {
            logger.LogWarning("Compaction failed for session {SessionId}: agent_data block not found", agentId);
            return false;
        }

        // ── Build output: preserved FIRST, summary LAST ──
        // Because the summary was generated with full context (including preserved zones),
        // it sits at the end as a high-level reference for everything that came before.
        var nextNumber = agentBlock.Number + 1;
        var newBlocks = new List<MemoryBlock>();

        // Preserved zones first (granular recent context)
        foreach (var block in preserved)
        {
            block.Number = nextNumber++;
            newBlocks.Add(block);
        }

        // Summary last (covers everything including preserved zones)
        if (summary is not null)
        {
            var block = MemoryBlock.AgentMessage(summary, model: model);
            block.Number = nextNumber++;
            newBlocks.Add(block);
        }

        // Atomically replace history: hard-delete everything after agent_data,
        // soft-delete old unprotected blocks, insert new order
        await blockStore.ReplaceBlocksSinceAsync(
            agentId,
            fromNumber: agentBlock.Number + 1,
            newBlocks,
            nextNumber,
            softDeleteNums: allOldNums.Count > 0 ? allOldNums : null);

        logger.LogInformation("Compacted session {SessionId}: struct-cut summary (last), {PreservedCount} preserved blocks (first), {SoftDeletedCount} soft-deleted",
            agentId, preserved.Count, allOldNums.Count);
        return true;
    }
}
