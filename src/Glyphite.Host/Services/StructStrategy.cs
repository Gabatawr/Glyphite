using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Structured compaction strategy.
///
/// Sends ALL content from agent_data (excluding it) — every turn group, already compressed zones
/// included — into a single LLM call with a structured template
/// (## Goal, ## Progress, ## Key Decisions, ## Relevant Files, ## Next Steps).
/// The LLM sees the full conversation for the most informed summary.
///
/// After summarisation:
///   - Old turns are hard-deleted by <c>ReplaceBlocksSinceAsync</c>; they survive only inside the
///     summary.
///   - Already compressed zones (summary+turn from previous compactions) pass through untouched.
///   - The last 2 turns are preserved intact for granular access.
///   - If summarization fails, the to-compress turns are preserved as-is, in their original place
///     (fallback — no data loss and no reordering).
///
/// Block order in DB/context is always chronological:
///   agent_data → compressed zones → to-compress turns (fallback only) → safe zones
///   → struct summary (covers everything) → turn marker
///
/// The summary describes the whole session, so it sits last as a high-level recap while the safe
/// zones above it keep the most recent turn-by-turn detail.
/// </summary>
internal static class StructStrategy
{
    /// <summary>Execute the struct compaction.</summary>
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

        // Same guard as fibo, and for the same reason: the header is the splice anchor, so checking
        // it costs nothing now and avoids paying for a summarization that cannot be stored.
        var agentBlock = CompactionService.FindAgentDataBlock(blocks, agentId, logger);
        if (agentBlock is null)
            return false;

        // GroupByTurns keeps the header in its own group, so "count <= 3" means "fewer than 3
        // turns": 2 stay safe, the rest get summarized. Anything smaller has nothing to summarize.
        var turnGroups = CompactionService.GroupByTurns(blocks);
        if (turnGroups.Count <= 3)
            return false;

        var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);
        var zoneClass = CompactionService.ClassifyZones(turnGroups, threshold, logger);
        var safeGroups = zoneClass.SafeGroups;
        var compressedGroups = zoneClass.CompressedGroups;
        var toCompressGroups = zoneClass.ToCompressGroups;

        if (toCompressGroups.Count == 0)
            return false;

        // ── Build LLM input: EVERYTHING except agent_data ──
        // Send ALL blocks — group 0 (excl. agent_data), compressed, to_compress, and safe.
        // The LLM sees the complete picture for the most comprehensive summary.
        // Safe zones are preserved intact after compaction, but included in LLM input
        // so the summary covers the entire session.
        // Group 0 is agent_data alone (GroupByTurns invariant) — the header carries no history.
        // Everything else lives in a real turn group, so it is listed below exactly once.
        var summarizeBlocks = new List<MemoryBlock>();

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

        if (summarizeBlocks.Count == 0)
            return false;

        logger.LogInformation("Compacting session {SessionId}: {TotalBlocks} blocks, {AllBlocksToSummarize} blocks to summarize (struct, unfiltered), threshold {Threshold}%",
            agentId, blocks.Count, summarizeBlocks.Count, compOpts.AutoThreshold);

        // ── Single LLM summarization with structured template ──
        var (summary, hit, miss, summaryOutput, summaryReasoning) = await CompactionService.SummarizeZoneAsync(
            agentId, summarizeBlocks, model, chatClient, agentStore, logger, structured: true);

        if (summary is null && safeGroups.Count == 0 && toCompressGroups.Count == 0)
            return false;

        // ── Build output ──
        // Order must stay chronological: compressed (oldest, already summarized) → the turns this
        // run replaces (fallback keeps them verbatim, in their original place) → safe zones (most
        // recent, granular) → the summary. The summary covers the whole session, so it sits last as
        // a recap instead of in place of the turns it describes.
        var nextNumber = agentBlock.Number + 1;
        var newBlocks = new List<MemoryBlock>();

        // Already compressed zones — pass through untouched (oldest history)
        foreach (var group in compressedGroups)
        {
            foreach (var block in group)
            {
                block.Number = nextNumber++;
                newBlocks.Add(block);
            }
        }

        // Summarization failed: keep the to-compress turns verbatim. They are the oldest turns in
        // the transcript, so they go before the safe zones — putting them after would hand the next
        // compaction a reordered history and make the oldest turn look like the newest one.
        if (summary is null)
        {
            foreach (var group in toCompressGroups)
            {
                foreach (var block in group)
                {
                    block.Number = nextNumber++;
                    newBlocks.Add(block);
                }
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

        // Summary — covers everything above (including the safe zones), so it goes last.
        if (summary is not null)
        {
            var block = MemoryBlock.AgentMessage(summary, model: model);
            block.Number = nextNumber++;
            block.Compressed = true;
            newBlocks.Add(block);

            // The marker records the summarization call's real usage (`hit`/`miss` = its prompt,
            // `out_` = billed output including `reasoning`) plus the size of what was stored. A
            // summary is pure result text: all of its weight is `stored_text`.
            var stored = CompactionService.EstimateZoneTokens([block]);
            var turnBlock = MemoryBlock.TurnMarker(
                JsonSerializer.Serialize(new
                {
                    hit, miss, out_ = summaryOutput, reasoning = summaryReasoning,
                    stored, stored_reasoning = 0L, stored_text = stored, stored_tool = 0L
                }));
            turnBlock.Number = nextNumber++;
            newBlocks.Add(turnBlock);
        }

        // Atomically replace history: hard-delete everything after agent_data, insert new order
        await blockStore.ReplaceBlocksSinceAsync(
            agentId,
            fromNumber: agentBlock.Number + 1,
            newBlocks,
            nextNumber);

        logger.LogInformation("Compacted session {SessionId}: {SummaryCount} summary, {CompressedCount} compressed preserved, {FallbackCount} fallback blocks, {SafeCount} safe preserved (struct)",
            agentId, summary is null ? 0 : 1, compressedGroups.Count, summary is null ? toCompressGroups.Sum(g => g.Count) : 0, safeGroups.Count);
        return true;
    }
}
