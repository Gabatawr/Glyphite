using System.Text;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

    /// <summary>
    /// Auto-compaction service: after each turn, if context usage exceeds the configured threshold,
    /// groups all blocks into Fibonacci-sized zones by turn, strips unprotected blocks from old zones
    /// (zone 3+), summarizes them via LLM, and replaces history with summaries + intact new zones.
    /// Zones 1-2 (the two newest turns) are preserved entirely intact — all blocks, including tool
    /// calls, auto_tool results, todo blocks, and agent reasoning stay as-is.
    /// </summary>
    public class CompactionService
    {
        private readonly IBlockStore _blockStore;
        private readonly IAgentStore _agentStore;
        private readonly IConfigService _cfgService;
        private readonly IChatClient _chatClient;
        private readonly ILogger _logger;

        public CompactionService(
            IBlockStore blockStore,
            IAgentStore agentStore,
            IConfigService cfgService,
            IChatClient chatClient,
            ILogger<CompactionService> logger)
        {
            _blockStore = blockStore;
            _agentStore = agentStore;
            _cfgService = cfgService;
            _chatClient = chatClient;
            _logger = logger;
        }

        /// <summary>Fast check — no LLM calls. Determines if compaction is likely needed.</summary>
        public async Task<bool> ShouldCompactAsync(string agentId, int contextWindow)
        {
            var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, agentId);
            if (!compOpts.AutoCompress)
                return false;

            var blocks = await _blockStore.LoadBlocksAsync(agentId);
            if (blocks.Count <= 1)
                return false;

            var lastUsage = await _agentStore.GetLastUsageAsync(agentId);
            var lastRequestTokens = lastUsage.LastHit + lastUsage.LastMiss;
            var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);

            // If usage tracking returned 0 (unknown provider / parsing failed),
            // fall back to an estimate based on block content length.
            if (lastRequestTokens == 0)
            {
                var estimatedTokens = blocks.Sum(b => (b.Content?.Length ?? 0) / 4);
                if (estimatedTokens < threshold)
                    return false;
            }
            else if (lastRequestTokens < threshold)
            {
                return false;
            }

            // Group + zone check (still fast — in-memory)
            var turnGroups = GroupByTurns(blocks);
            return turnGroups.Count > 1 && DistributeFibonacci(turnGroups).Count > 2;
        }

        public async Task<bool> CompactAsync(string agentId, int contextWindow)
        {
            var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, agentId);
            var blocks = await _blockStore.LoadBlocksAsync(agentId);
            if (blocks.Count <= 1)
                return false;

            // 1. Group ALL blocks into turns
            var turnGroups = GroupByTurns(blocks);
            if (turnGroups.Count <= 1)
                return false;

            // 2. Distribute turn groups into Fibonacci zones
            var zones = DistributeFibonacci(turnGroups);
            if (zones.Count <= 2)
                return false;

            var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>(MemoryOptions.Section, agentId);
            var protectedTypes = new HashSet<BlockType>(
                memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));

            var isSubagentTool = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "subagent_run", "subagent_use" };

            var model = await _agentStore.GetAgentModelAsync(agentId);
            var summarizeCount = zones.Count - 2;

            var allUnprotectedNums = new HashSet<double>();
            var zoneProtectedBlocks = new List<List<MemoryBlock>>();

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

            // Log compaction start
            var totalBlocks = blocks.Count;
            _logger.LogInformation("Compacting session {SessionId}: {TotalBlocks} blocks, {ZoneCount} old zones to summarize, threshold {Threshold}% of {ContextWindow}",
                agentId, totalBlocks, summarizeCount, compOpts.AutoThreshold, contextWindow);

            // 4. Summarize all old zones via LLM in parallel — each zone is independent.
            //    Run BEFORE deletion — if summarization fails, no data is lost.
            var zoneTasks = new List<(List<MemoryBlock> zone, Task<string?> task)>();
            foreach (var zone in zoneProtectedBlocks)
            {
                if (zone.Count == 0) continue;
                zoneTasks.Add((zone, SummarizeSingleZoneAsync(agentId, zone, model)));
            }

            var summaries = new List<string>();
            var summarizedFallback = new List<MemoryBlock>(); // protected blocks from zones whose summarization failed
            foreach (var (zone, task) in zoneTasks)
            {
                var summary = await task;
                if (summary is not null)
                    summaries.Add(summary);
                else
                    summarizedFallback.AddRange(zone);
            }

            // 5. Collect preserved blocks from the two newest zones (completely intact)
            var preserved = new List<MemoryBlock>();
            for (var i = summarizeCount; i < zones.Count; i++)
                preserved.AddRange(zones[i]);

            if (summaries.Count == 0 && preserved.Count == 0 && summarizedFallback.Count == 0)
                return false;

            // 6. Find agent_data block
            var agentBlock = blocks.FirstOrDefault(b => b.Type == BlockType.agent_data);
            if (agentBlock is null)
            {
                _logger.LogWarning("Compaction failed for session {SessionId}: agent_data block not found", agentId);
                return false;
            }

            // Build compacted history: summaries (old zones) + summary failure fallback + preserved original blocks
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

            // 7. Atomically replace history: soft-delete unprotected old-zone blocks,
            //    hard-delete everything after agent_data, insert compacted history.
            //    All in one transaction — if anything fails, nothing is lost.
            await _blockStore.ReplaceBlocksSinceAsync(
                agentId,
                fromNumber: agentBlock.Number + 1,
                newBlocks,
                nextNumber,
                softDeleteNums: allUnprotectedNums.Count > 0 ? allUnprotectedNums : null);

            _logger.LogInformation("Compacted session {SessionId}: {SummaryCount} summaries, {FallbackCount} fallback blocks, {PreservedCount} preserved, {SoftDeletedCount} soft-deleted",
                agentId, summaries.Count, summarizedFallback.Count, preserved.Count, allUnprotectedNums.Count);
            return true;
        }

    /// <summary>
    /// Group blocks into turns. The first group contains agent_data alone.
    /// Each subsequent group starts at a user_message/agent_task or turn marker and ends at the next turn marker or end.
    /// </summary>
    private static List<List<MemoryBlock>> GroupByTurns(List<MemoryBlock> blocks)
    {
        var ordered = blocks.OrderBy(b => b.Number).ToList();
        var groups = new List<List<MemoryBlock>>();
        var current = new List<MemoryBlock>();

        foreach (var block in ordered)
        {
            if (block.Type == BlockType.agent_data)
            {
                // agent_data starts the first group
                if (current.Count > 0)
                    groups.Add(current);
                current = new List<MemoryBlock> { block };
            }
            else if (block.Type == BlockType.turn)
            {
                // Turn marker ends the current group
                current.Add(block);
                groups.Add(current);
                current = new List<MemoryBlock>();
            }
            else
            {
                current.Add(block);
            }
        }

        // Flush any remaining blocks
        if (current.Count > 0)
            groups.Add(current);

        return groups;
    }

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
                // Each zone is a flat list of blocks (user_messages, agent_messages, turn markers)
                zones.Add(zone);
            }

            // Next Fibonacci number
            var next = fibPrev + fibCurr;
            fibPrev = fibCurr;
            fibCurr = next;
        }

        // zones are newest-first, reverse to be oldest-first for display
        zones.Reverse();
        return zones;
    }


    private async Task<string?> SummarizeSingleZoneAsync(string agentId, List<MemoryBlock> blocks, string? model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize this conversation zone, preserving key decisions, findings, code changes, important context. Pay attention to the volume of content — the more messages in the zone, the more detail is expected. Do not rush past important user requests, agent responses, tool results, or subagent outputs.");
        sb.AppendLine();

        foreach (var block in blocks)
        {
            sb.AppendLine(block.ToContextString());
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a precise summarizer for a coding assistant conversation. Summarize the zone below. Respond in English. Adjust detail to the zone volume — a large zone warrants a proportionally longer summary."),
            new(ChatRole.User, sb.ToString())
        };

        var chatOpts = new ChatOptions
        {
            ModelId = model
        };

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, chatOpts);
            var assistantMsg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            var text = assistantMsg?.Text?.Trim();

            // Extract usage from response and record to main session immediately
            if (response.RawRepresentation is not null)
            {
                try
                {
                    using var doc = UsageParser.Normalize(response.RawRepresentation);
                    if (doc is not null)
                    {
                        var (hit, miss, output) = UsageParser.Parse(doc);
                        if (hit > 0 || miss > 0 || output > 0)
                            await _agentStore.RecordUsageAsync(agentId, hit, miss, output, model: model);
                    }
                }
                catch { _logger.LogWarning("Failed to parse usage from summarization response"); }
            }

            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
