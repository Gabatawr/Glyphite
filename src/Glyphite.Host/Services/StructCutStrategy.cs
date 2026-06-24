using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Structured-cut compaction strategy.
///
/// Like <c>fibo-parts</c>, it cleans old turns (3+) by stripping unprotected blocks
/// (tool results, auto_tool, reasoning) while preserving protected block types and
/// subagent tool results. Unlike <c>fibo-parts</c>, it then summarizes ALL remaining
/// content from the old turns in a single LLM call with a structured template
/// (## Goal, ## Progress, ## Key Decisions, ## Relevant Files, ## Next Steps)
/// instead of multiple per-zone free-form summaries.
///
/// The last 2 turns are always preserved intact.
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

        // turnGroups[0] = agent_data group
        // turnGroups[1..^2] = old turn groups (3+) — strip unprotected, keep subagent tools
        // turnGroups[^2..] = last 2 turn groups (preserved intact)

        var oldGroups = turnGroups.Skip(1).Take(turnGroups.Count - 3).ToList();
        var preserveGroups = turnGroups.TakeLast(2).ToList();

        if (oldGroups.Count == 0)
            return false;

        // --- same filtering as fibo-parts: strip unprotected, keep subagent tools ---
        var summarizeBlocks = new List<MemoryBlock>();
        var allOldNums = new HashSet<double>();

        foreach (var group in oldGroups)
        {
            foreach (var b in group)
            {
                if (protectedTypes.Contains(b.Type) ||
                    (b.Type == BlockType.tool && b.ToolName is not null && isSubagentTool.Contains(b.ToolName)))
                {
                    summarizeBlocks.Add(b);
                }
                else
                {
                    allOldNums.Add(b.Number);
                }
            }
        }

        if (summarizeBlocks.Count == 0)
            return false;

        var totalBlocks = blocks.Count;
        logger.LogInformation("Compacting session {SessionId}: {TotalBlocks} blocks, {OldTurnCount} old turns, {SummarizeBlocks} blocks to summarize (struct-cut), threshold {Threshold}%",
            agentId, totalBlocks, oldGroups.Count, summarizeBlocks.Count, compOpts.AutoThreshold);

        // --- single LLM summarization with structured template (the only difference from fibo-parts) ---
        var summary = await CompactionService.SummarizeZoneAsync(
            agentId, summarizeBlocks, model, chatClient, agentStore, logger, structured: true);

        // Collect preserved blocks from the two newest turn groups (intact)
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

        var nextNumber = agentBlock.Number + 1;
        var newBlocks = new List<MemoryBlock>();

        if (summary is not null)
        {
            var block = MemoryBlock.AgentMessage(summary, model: model);
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
            softDeleteNums: allOldNums.Count > 0 ? allOldNums : null);

        logger.LogInformation("Compacted session {SessionId}: struct-cut summary, {PreservedCount} preserved blocks, {SoftDeletedCount} soft-deleted",
            agentId, preserved.Count, allOldNums.Count);
        return true;
    }
}
