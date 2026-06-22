using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

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

        public CompactionService(
            IBlockStore blockStore,
            IAgentStore agentStore,
            IConfigService cfgService,
            IChatClient chatClient)
        {
            _blockStore = blockStore;
            _agentStore = agentStore;
            _cfgService = cfgService;
            _chatClient = chatClient;
        }

        public async Task<bool> TryCompactAsync(string sessionId, int contextWindow)
        {
            var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>("Compression", sessionId);
            if (!compOpts.AutoCompress)
                return false;

            var blocks = await _blockStore.LoadBlocksAsync(sessionId);
            if (blocks.Count <= 1) // Only agent_data
                return false;

            // Use real tokens from the last API request (lastHit + lastMiss from the last turn)
            var lastUsage = await _agentStore.GetLastUsageAsync(sessionId);
            var lastRequestTokens = lastUsage.LastHit + lastUsage.LastMiss;
            var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);
            if (lastRequestTokens < threshold)
                return false;

            // 1. Group ALL blocks into turns (including tool, auto_tool, reasoning, todo, etc.)
            var turnGroups = GroupByTurns(blocks);
            if (turnGroups.Count <= 1) // Just agent_data + nothing
                return false;

            // 2. Distribute turn groups into Fibonacci zones (from newest backwards)
            var zones = DistributeFibonacci(turnGroups);
            if (zones.Count <= 2) // Two or fewer zones — nothing old enough to compact
                return false;

            // zones are in oldest-first order:
            //   zones[0..^3] = old zones (zone 3+) → strip unprotected + summarize
            //   zones[^2..]   = two newest zones (zone 1-2) → keep intact (all blocks preserved)

            var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>("Memory", sessionId);
            var protectedTypes = new HashSet<BlockType>(
                memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));

            // Also keep subagent tool results — they get included in summarization
            var isSubagentTool = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "subagent_run", "subagent_use" };

            var model = await _agentStore.GetAgentModelAsync(sessionId);
            var summarizeCount = zones.Count - 2; // all zones except the last two (newest)

            // 3. Strip unprotected blocks from old zones, collect protected + subagent blocks for summarization
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

            // 4. Summarize each old zone via LLM (using only protected blocks)
            //    Run BEFORE deletion — if summarization fails, no data is lost.
            var summaries = new List<string>();
            for (var i = 0; i < zoneProtectedBlocks.Count; i++)
            {
                if (zoneProtectedBlocks[i].Count == 0)
                    continue;

                var summary = await SummarizeSingleZoneAsync(zoneProtectedBlocks[i], model);
                if (summary is not null)
                    summaries.Add(summary);
            }

            // 5. Collect preserved blocks from the two newest zones (completely intact)
            var preserved = new List<MemoryBlock>();
            for (var i = summarizeCount; i < zones.Count; i++)
                preserved.AddRange(zones[i]);

            if (summaries.Count == 0 && preserved.Count == 0)
                return false;

            // Remove all unprotected blocks from old zones in one batch
            // Done AFTER successful summarization — if LLM failed, data stays intact.
            if (allUnprotectedNums.Count > 0)
                await _blockStore.RemoveBlocksAsync(sessionId, b => allUnprotectedNums.Contains(b.Number));

            // 6. Find agent_data block, delete everything after it, insert compacted history
            var agentBlock = blocks.First(b => b.Type == BlockType.agent_data);
            await _blockStore.DeleteBlocksSinceAsync(sessionId, agentBlock.Number + 1);

            var nextNumber = agentBlock.Number + 1;
            var newBlocks = new List<MemoryBlock>();

            // First insert summaries (old zones, oldest-first)
            foreach (var summary in summaries)
            {
                var block = MemoryBlock.AgentMessage(summary, model: model);
                block.Number = nextNumber++;
                newBlocks.Add(block);
            }

            // Then insert preserved original blocks (newest zones, completely intact)
            foreach (var block in preserved)
            {
                block.Number = nextNumber++;
                newBlocks.Add(block);
            }

            if (newBlocks.Count > 0)
                await _blockStore.AppendBlocksAsync(sessionId, newBlocks, nextNumber);

            return true;
        }

    /// <summary>
    /// Group blocks into turns. The first group contains agent_data alone.
    /// Each subsequent group starts at a user_message or turn marker and ends at the next turn marker or end.
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


    private async Task<string?> SummarizeSingleZoneAsync(List<MemoryBlock> blocks, string? model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize this conversation zone concisely in 2-4 sentences, preserving key decisions, findings, code changes, and important context.");
        sb.AppendLine();

        foreach (var block in blocks)
        {
            sb.AppendLine(block.ToContextString());
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a precise summarizer for a coding assistant conversation. Provide a concise summary of the given conversation zone."),
            new(ChatRole.User, sb.ToString())
        };

        var chatOpts = new ChatOptions
        {
            ModelId = model,
            Temperature = 0.3f,
            MaxOutputTokens = 512
        };

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, chatOpts);
            var assistantMsg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            var text = assistantMsg?.Text?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
