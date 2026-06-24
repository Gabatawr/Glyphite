using System.Linq;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Auto-compaction service: after each turn, if context usage exceeds the configured threshold,
/// compresses old conversation history via LLM summarization. Supports two strategies:
///
/// <c>fibo-parts</c> (default): groups blocks into Fibonacci-sized zones by turn, strips unprotected
/// blocks from old zones (zone 3+), summarizes protected blocks via LLM, replaces history with
/// summaries + intact new zones. Zones 1-2 (the two newest turns) are preserved entirely intact.
///
/// <c>struct-cut</c>: sends ALL content (including unprotected tool results, auto_tool, reasoning)
/// from agent_data to the save boundary (last 2 turns) into a single LLM call with a structured
/// template. After summarization, unprotected old blocks are soft-deleted. The model sees the full
/// picture for a more informed summary. One summary block replaces all old history.
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

        // Token threshold check
        var lastUsage = await _agentStore.GetLastUsageAsync(agentId);
        var lastRequestTokens = lastUsage.LastHit + lastUsage.LastMiss;
        var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);

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

        var strategy = PickStrategy(compOpts);
        var turnGroups = GroupByTurns(blocks);

        if (strategy == "struct-cut")
            return turnGroups.Count > 3; // agent_data + at least 3 turn groups

        // fibo-parts: need at least 2 Fibonacci zones after agent_data
        return turnGroups.Count > 1 && FiboPartsStrategy.DistributeFibonacci(turnGroups).Count > 2;
    }

    /// <summary>Randomly pick one of the enabled strategies.</summary>
    internal static string PickStrategy(CompressionOptions compOpts)
    {
        var enabled = compOpts.Strategies
            .Where(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToArray();

        if (enabled.Length == 1)
            return enabled[0];

        return enabled[Random.Shared.Next(enabled.Length)];
    }

    /// <summary>Execute compaction using a randomly selected enabled strategy.</summary>
    public async Task<bool> CompactAsync(string agentId, int contextWindow)
    {
        var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, agentId);
        var strategy = PickStrategy(compOpts);
        var blocks = await _blockStore.LoadBlocksAsync(agentId);
        var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>(MemoryOptions.Section, agentId);
        var model = await _agentStore.GetAgentModelAsync(agentId);

        if (strategy == "struct-cut")
        {
            return await StructCutStrategy.CompactAsync(
                agentId, compOpts, memOpts, blocks, model,
                _blockStore, _chatClient, _agentStore, _logger);
        }

        return await FiboPartsStrategy.CompactAsync(
            agentId, compOpts, memOpts, blocks, model,
            _blockStore, _chatClient, _agentStore, _logger);
    }

    // ===== Shared helpers (used by both strategies) =====

    /// <summary>
    /// Summarize a set of blocks via LLM. When <paramref name="structured"/> is true,
    /// uses a full-session structured template (## Goal, ## Progress, ## Key Decisions, ## Relevant Files, ## Next Steps).
    /// When false, uses a zone-specific structured template (## Topics, ## Key Actions, ## Results, ## State Changes, ## Open / Carried Over).
    /// </summary>
    internal static async Task<string?> SummarizeZoneAsync(
        string agentId,
        List<MemoryBlock> blocks,
        string? model,
        IChatClient chatClient,
        IAgentStore agentStore,
        ILogger logger,
        bool structured)
    {
        var sb = new StringBuilder();

        if (structured)
        {
            sb.AppendLine("Summarize this conversation using the following structured sections. Each section is required — write \"None\" if empty.");
            sb.AppendLine();
            sb.AppendLine("## Goal");
            sb.AppendLine("What was the overall goal or objective of this part of the conversation?");
            sb.AppendLine();
            sb.AppendLine("## Progress");
            sb.AppendLine("What was accomplished? What is in progress? What is blocked or pending?");
            sb.AppendLine();
            sb.AppendLine("## Key Decisions");
            sb.AppendLine("What architectural, design, or implementation decisions were made? Include reasoning.");
            sb.AppendLine();
            sb.AppendLine("## Relevant Files / Directories");
            sb.AppendLine("Which files or directories were discussed, modified, or created? Include paths.");
            sb.AppendLine();
            sb.AppendLine("## Next Steps");
            sb.AppendLine("What remains to be done? What are the open questions or follow-up tasks?");
            sb.AppendLine();
            sb.AppendLine("Keep bullet points concise. Preserve exact file paths, command examples, and error messages where relevant.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Summarize this conversation zone using the following structured sections. Each section is required — write \"None\" if empty.");
            sb.AppendLine();
            sb.AppendLine("## Topics");
            sb.AppendLine("What topics or tasks were discussed in this zone? List them briefly.");
            sb.AppendLine();
            sb.AppendLine("## Key Actions");
            sb.AppendLine("What tools were called, what commands were run, what files were read or modified?");
            sb.AppendLine();
            sb.AppendLine("## Results");
            sb.AppendLine("What was accomplished, found, or confirmed? Include relevant outputs, error messages, or command results.");
            sb.AppendLine();
            sb.AppendLine("## State Changes");
            sb.AppendLine("Which files, configurations, or data were created, modified, or deleted? Include exact paths.");
            sb.AppendLine();
            sb.AppendLine("## Open / Carried Over");
            sb.AppendLine("What remains unfinished, pending, or carried over to subsequent turns?");
            sb.AppendLine();
        }

        foreach (var block in blocks)
        {
            sb.AppendLine(block.ToContextString());
        }

        var systemPrompt = structured
            ? "You are a precise structured summarizer for a coding assistant conversation. Respond in English. Use the exact section headers provided (## Goal, ## Progress, ## Key Decisions, ## Relevant Files / Directories, ## Next Steps). Keep each section concise but complete."
            : "You are a precise zone summarizer for a coding assistant conversation. Respond in English. Use the exact section headers provided (## Topics, ## Key Actions, ## Results, ## State Changes, ## Open / Carried Over). Keep each section concise but complete. Preserve exact file paths, error messages, and command examples.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString())
        };

        var chatOpts = new ChatOptions
        {
            ModelId = model
        };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, chatOpts);
            var assistantMsg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            var text = assistantMsg?.Text?.Trim();

            if (response.RawRepresentation is not null)
            {
                try
                {
                    using var doc = UsageParser.Normalize(response.RawRepresentation);
                    if (doc is not null)
                    {
                        var (hit, miss, output) = UsageParser.Parse(doc);
                        if (hit > 0 || miss > 0 || output > 0)
                            await agentStore.RecordUsageAsync(agentId, hit, miss, output, model: model);
                    }
                }
                catch { logger.LogWarning("Failed to parse usage from summarization response"); }
            }

            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Group blocks into turns. The first group contains agent_data alone.
    /// Each subsequent group starts at a user_message/agent_task or turn marker and ends at the next turn marker or end.
    /// </summary>
    internal static List<List<MemoryBlock>> GroupByTurns(List<MemoryBlock> blocks)
    {
        var ordered = blocks.OrderBy(b => b.Number).ToList();
        var groups = new List<List<MemoryBlock>>();
        var current = new List<MemoryBlock>();

        foreach (var block in ordered)
        {
            if (block.Type == BlockType.agent_data)
            {
                if (current.Count > 0)
                    groups.Add(current);
                current = new List<MemoryBlock> { block };
            }
            else if (block.Type == BlockType.turn)
            {
                current.Add(block);
                groups.Add(current);
                current = new List<MemoryBlock>();
            }
            else
            {
                current.Add(block);
            }
        }

        if (current.Count > 0)
            groups.Add(current);

        return groups;
    }
}
