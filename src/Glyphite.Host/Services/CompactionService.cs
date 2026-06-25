using System.Text.Json;
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
/// <c>fibo</c> (default): groups blocks into Fibonacci-sized zones by turn, strips unprotected
/// blocks from old zones (zone 3+), summarizes protected blocks via LLM, replaces history with
/// summaries + intact new zones. Zones 1-2 (the two newest turns) are preserved entirely intact.
///
/// <c>struct</c>: sends ALL content (including unprotected tool results, auto_tool, reasoning)
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

    /// <summary>Randomly pick one of the enabled strategies.</summary>
    public static string PickStrategy(CompressionOptions compOpts)
    {
        var enabled = compOpts.Strategies
            .Where(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToArray();

        if (enabled.Length == 1)
            return enabled[0];

        return enabled[Random.Shared.Next(enabled.Length)];
    }

    /// <summary>Evaluate compaction status without executing compaction. Loads blocks, classifies zones,
    /// picks strategy, and returns threshold/mode info. Use at end-of-turn for prompt coloring or in /compression command.
    /// When <paramref name="isManual"/> is false (auto-caller), returns WillCompact=false if AutoCompress is disabled
    /// in config. Manual callers (/compression) pass isManual:true to always get full status regardless of AutoCompress.</summary>
    public async Task<CompactionStatus> EvaluateCompactionStatusAsync(string agentId, int contextWindow, bool isManual = false)
    {
        var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, agentId);

        var strategy = PickStrategy(compOpts);

        // Auto paths: if AutoCompress is disabled, signal no compaction
        if (!isManual && !compOpts.AutoCompress)
            return new CompactionStatus(false, false, strategy, "none");

        var threshold = (int)(compOpts.AutoThreshold / 100.0 * contextWindow);

        // Check threshold via last usage
        var lastUsage = await _agentStore.GetLastUsageAsync(agentId);
        var lastRequestTokens = lastUsage.LastHit + lastUsage.LastMiss;
        var isThresholdExceeded = lastRequestTokens >= threshold;

        // Classify zones to determine if compaction will actually happen
        var blocks = await _blockStore.LoadBlocksAsync(agentId);
        var turnGroups = GroupByTurns(blocks);
        var zoneClass = ClassifyZones(turnGroups, threshold);

        var willCompact = zoneClass.ToCompressGroups.Count > 0;

        var mode = zoneClass.IsSafeHardMode ? "safe+" : "";
        mode += zoneClass.IsHardMode ? "hard" : "soft";
        if (!willCompact) mode = "none";

        return new CompactionStatus(isThresholdExceeded, willCompact, strategy, mode);
    }

    /// <summary>Execute compaction. If strategy is null, picks randomly from enabled strategies.</summary>
    public async Task<bool> CompactAsync(string agentId, int contextWindow, string? strategy = null)
    {
        var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, agentId);
        strategy ??= PickStrategy(compOpts);
        var blocks = await _blockStore.LoadBlocksAsync(agentId);
        var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>(MemoryOptions.Section, agentId);
        var model = await _agentStore.GetAgentModelAsync(agentId);

        if (strategy == "struct")
        {
            return await StructStrategy.CompactAsync(
                agentId, compOpts, memOpts, blocks, model,
                _blockStore, _chatClient, _agentStore, _logger, contextWindow);
        }

        return await FiboStrategy.CompactAsync(
            agentId, compOpts, memOpts, blocks, model,
            _blockStore, _chatClient, _agentStore, _logger, contextWindow);
    }

    // ===== Shared helpers (used by both strategies) =====

    /// <summary>
    /// Summarize a set of blocks via LLM. When <paramref name="structured"/> is true,
    /// uses a full-session structured template (## Goal, ## Progress, ## Key Decisions, ## Relevant Files, ## Next Steps).
    /// When false, uses a zone-specific structured template (## Topics, ## Key Actions, ## Results, ## State Changes, ## Open / Carried Over).
    /// Returns the summary text and usage stats from the LLM call.
    /// </summary>
    internal static async Task<(string? Summary, long Hit, long Miss, long Output)> SummarizeZoneAsync(
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

            long hit = 0, miss = 0, output = 0;

            if (response.RawRepresentation is not null)
            {
                try
                {
                    using var doc = UsageParser.Normalize(response.RawRepresentation);
                    if (doc is not null)
                    {
                        (hit, miss, output) = UsageParser.Parse(doc);
                        if (hit > 0 || miss > 0 || output > 0)
                            await agentStore.RecordUsageAsync(agentId, hit, miss, output, model: model);
                    }
                }
                catch { logger.LogWarning("Failed to parse usage from summarization response"); }
            }

            return (string.IsNullOrEmpty(text) ? null : text, hit, miss, output);
        }
        catch
        {
            return (null, 0, 0, 0);
        }
    }

    /// <summary>Fast check — are there any uncompressed turn groups beyond safe zones?</summary>
    public static bool HasToCompressZones(List<List<MemoryBlock>> turnGroups)
    {
        if (turnGroups.Count <= 1)
            return false;

        for (var i = turnGroups.Count - 1; i >= 1; i--)
        {
            var rankFromNewest = turnGroups.Count - 1 - i;
            if (rankFromNewest < 2)
                continue;
            if (!turnGroups[i].Any(b => b.Compressed))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Group blocks into turns. The first group contains agent_data alone.
    /// Each subsequent group starts at a user_message/agent_task or turn marker and ends at the next turn marker or end.
    /// </summary>
    public static List<List<MemoryBlock>> GroupByTurns(List<MemoryBlock> blocks)
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

    /// <summary>Result of zone classification with all three zone groups.</summary>
    public sealed record ZoneClassification(
        List<List<MemoryBlock>> SafeGroups,
        List<List<MemoryBlock>> CompressedGroups,
        List<List<MemoryBlock>> ToCompressGroups,
        int Threshold,
        bool IsHardMode,
        bool IsSafeHardMode);

    /// <summary>Compaction evaluation result — threshold check, whether compaction will run, strategy and mode.</summary>
    public sealed record CompactionStatus(
        bool IsThresholdExceeded,
        bool WillCompact,
        string Strategy,
        string Mode);

    /// <summary>Classify turn groups into safe/compressed/to_compress and apply hard mode checks.</summary>
    public static ZoneClassification ClassifyZones(
        List<List<MemoryBlock>> turnGroups,
        int threshold,
        ILogger? logger = null)
    {
        var safeGroups = new List<List<MemoryBlock>>();
        var compressedGroups = new List<List<MemoryBlock>>();
        var toCompressGroups = new List<List<MemoryBlock>>();

        for (var i = turnGroups.Count - 1; i >= 1; i--)
        {
            var group = turnGroups[i];
            var rankFromNewest = turnGroups.Count - 1 - i;
            if (rankFromNewest < 2)
                safeGroups.Add(group);
            else if (group.Any(b => b.Compressed))
                compressedGroups.Add(group);
            else
                toCompressGroups.Add(group);
        }

        safeGroups.Reverse();
        compressedGroups.Reverse();
        toCompressGroups.Reverse();

        var isHardMode = false;
        var compressedTokens = SumCompressedOutput(compressedGroups);
        if (compressedTokens >= (int)(2.0 / 3.0 * threshold) && compressedGroups.Count > 0)
        {
            logger?.LogInformation("Hard mode: {CompressedTokens} compressed tokens >= 2/3 of threshold {Threshold}, recompressing {Count} compressed zones",
                compressedTokens, threshold, compressedGroups.Count);
            toCompressGroups.InsertRange(0, compressedGroups);
            compressedGroups.Clear();
            isHardMode = true;
        }

        var isSafeHardMode = false;
        var safeKept = CheckSafeZones(safeGroups, toCompressGroups, threshold);
        if (safeKept < 2)
        {
            logger?.LogInformation("Safe zone hard mode: kept {Kept} of 2 safe zones", safeKept);
            isSafeHardMode = true;
        }

        return new ZoneClassification(safeGroups, compressedGroups, toCompressGroups,
            threshold, isHardMode, isSafeHardMode);
    }

    /// <summary>Sum output tokens from turn markers in compressed groups (hard-mode threshold check).</summary>
    public static long SumCompressedOutput(List<List<MemoryBlock>> compressedGroups)
    {
        long total = 0;
        foreach (var group in compressedGroups)
            foreach (var block in group)
                if (block.Type == BlockType.turn && !string.IsNullOrEmpty(block.Content))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(block.Content);
                        if (doc.RootElement.TryGetProperty("out_", out var outProp) && outProp.TryGetInt64(out var outVal))
                            total += outVal;
                    }
                    catch { }
                }
        return total;
    }

    /// <summary>Estimate token count for a turn group by rendering blocks to context strings and dividing by 4.</summary>
    public static long EstimateZoneTokens(List<MemoryBlock> group)
    {
        long totalLen = 0;
        foreach (var block in group)
            totalLen += block.ToContextString().Length;
        return totalLen / 4;
    }

    /// <summary>Check safe zones against threshold. Moves oversized zones to to_compress.
    /// Returns the number of safe zones to keep (0, 1, or 2).</summary>
    public static int CheckSafeZones(
        List<List<MemoryBlock>> safeGroups,
        List<List<MemoryBlock>> toCompressGroups,
        int threshold)
    {
        if (safeGroups.Count < 2)
            return safeGroups.Count;

        var olderSize = EstimateZoneTokens(safeGroups[0]);
        var newerSize = EstimateZoneTokens(safeGroups[1]);

        if (newerSize >= threshold / 4L)
        {
            toCompressGroups.AddRange(safeGroups);
            safeGroups.Clear();
            return 0;
        }

        if (olderSize >= threshold / 4L)
        {
            toCompressGroups.Add(safeGroups[0]);
            safeGroups.RemoveAt(0);
            return 1;
        }

        return 2;
    }
}