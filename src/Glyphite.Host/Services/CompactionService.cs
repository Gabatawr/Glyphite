using System.Text;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Memory;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

/// <summary>
/// Auto-compaction service: after each turn, if context usage exceeds the configured threshold,
/// removes unprotected blocks, distributes remaining blocks into Fibonacci-sized zones by turn,
/// summarizes each zone via LLM, and replaces the history with the summaries.
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

        var totalTokens = blocks.Sum(b => BlockMemoryProvider.EstimateTokens(b.ToContextString()));
        var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);
        if (totalTokens < threshold)
            return false;

        // 1. Remove all non-protected blocks (tool, auto_tool, agent_reasoning, system*, todo, etc.)
        var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>("Memory", sessionId);
        var protectedTypes = new HashSet<BlockType>(
            memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));

        await _blockStore.RemoveBlocksAsync(sessionId, b => !protectedTypes.Contains(b.Type));

        // 2. Re-load remaining protected blocks
        blocks = await _blockStore.LoadBlocksAsync(sessionId);

        // 3. Group into turns: agent_data is its own group, then each group from a
        //    user_message (or turn marker) to the next turn marker.
        var turnGroups = GroupByTurns(blocks);
        if (turnGroups.Count <= 1) // Just agent_data, nothing to compact
            return false;

        // 4. Distribute turn groups into Fibonacci zones (from newest backwards)
        var zones = DistributeFibonacci(turnGroups);

        // 5. Build summarization prompt with all zones
        var model = await _agentStore.GetAgentModelAsync(sessionId);
        var summaries = await SummarizeZonesAsync(zones, model);

        if (summaries.Count == 0)
            return false;

        // 6. Find agent_data block, delete everything after it, insert summaries
        var agentBlock = blocks.First(b => b.Type == BlockType.agent_data);
        await _blockStore.DeleteBlocksSinceAsync(sessionId, agentBlock.Number + 1);

        var nextNumber = agentBlock.Number + 1;
        var summaryBlocks = new List<MemoryBlock>();
        for (var i = 0; i < summaries.Count; i++)
        {
            var block = MemoryBlock.AgentMessage(summaries[i], model: model);
            block.Number = nextNumber++;
            summaryBlocks.Add(block);
        }

        if (summaryBlocks.Count > 0)
            await _blockStore.AppendBlocksAsync(sessionId, summaryBlocks, nextNumber);

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

    /// <summary>
    /// Summarize all zones in a single LLM call.
    /// Returns one summary string per zone.
    /// </summary>
    private async Task<List<string>> SummarizeZonesAsync(List<List<MemoryBlock>> zones, string? model)
    {
        if (zones.Count == 0)
            return [];

        var sb = new StringBuilder();
        sb.AppendLine("Below are Zones from a coding session. Each Zone represents one or more conversation turns (user questions and agent responses).");
        sb.AppendLine("For EACH Zone, provide a concise 2-4 sentence summary preserving key decisions, findings, code changes, and important context.");
        sb.AppendLine("Be precise — focus on what was done, decided, or changed.");
        sb.AppendLine();

        for (var i = 0; i < zones.Count; i++)
        {
            sb.AppendLine($"=== ZONE {i + 1} ===");
            foreach (var block in zones[i])
            {
                var text = block.ToContextString();
                // Strip the block header line for brevity, keep content
                sb.AppendLine(text);
            }
            sb.AppendLine();
        }

        sb.AppendLine("Now respond with exactly one summary per zone, numbered:");
        for (var i = 1; i <= zones.Count; i++)
            sb.AppendLine($"SUMMARY {i}: ...");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a precise summarizer for a coding assistant conversation. Summarize each zone concisely, preserving key technical decisions, findings, and context."),
            new(ChatRole.User, sb.ToString())
        };

        var chatOpts = new ChatOptions
        {
            ModelId = model,
            Temperature = 0.3f,
            MaxOutputTokens = zones.Count * 256
        };

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(messages, chatOpts);
        }
        catch
        {
            return [];
        }

        var assistantMsg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        var responseText = assistantMsg?.Text ?? "";

        // Parse numbered summaries: "SUMMARY 1: ..."
        var summaries = new List<string>();
        StringBuilder? current = null;

        foreach (var line in responseText.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^SUMMARY\s+(\d+)[:\-.]\s*(.*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                if (current is not null)
                    summaries.Add(current.ToString().Trim());

                current = new StringBuilder(match.Groups[2].Value);
            }
            else if (current is not null)
            {
                if (current.Length > 0)
                    current.Append(' ');
                current.Append(trimmed);
            }
        }

        if (current is not null)
            summaries.Add(current.ToString().Trim());

        // Fallback: if parsing gave wrong count, return whole text as single summary
        if (summaries.Count == 0 && !string.IsNullOrEmpty(responseText))
            summaries.Add(responseText.Trim());

        return summaries;
    }
}
