using System.Text.Json;
using System.Text.Json.Nodes;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;

namespace Glyphite.Cli.Services;

public class ConsoleRenderer
{
    public ConsoleColor UserColor { get; set; } = ConsoleColor.DarkYellow;
    public ConsoleColor AgentColor { get; set; } = ConsoleColor.White;
    public ConsoleColor ReasoningColor { get; set; } = ConsoleColor.DarkGray;
    public ConsoleColor ToolResultColor { get; set; } = ConsoleColor.Gray;
    public ConsoleColor ToolCallColor { get; set; } = ConsoleColor.Cyan;
    public ConsoleColor ErrorColor { get; set; } = ConsoleColor.Red;

    /// <summary>Agent's working directory — replaces with <c>{cwd}</c> in tool arg display.</summary>
    public string? AgentCwd { get; set; }

    private ToolStreamingOptions _streamOpts;
    private readonly IConfigService _cfgService;

    /// <summary>Current (fresh) ToolStreamingOptions. Updated by RefreshAsync before each render cycle.</summary>
    public ToolStreamingOptions CurrentStreamOpts => _streamOpts;

    public ConsoleRenderer(IConfigService cfgService)
    {
        _streamOpts = new ToolStreamingOptions();
        _cfgService = cfgService;
    }

    /// <summary>Refresh ToolStreamingOptions from config. Call before rendering to pick up changes.</summary>
    public async Task RefreshAsync(string agentId)
    {
        _streamOpts = await _cfgService.GetOptionsAsync<ToolStreamingOptions>(ToolStreamingOptions.Section, agentId);
    }

    public void RenderBlock(MemoryBlock block, ref RenderState s)
    {
        switch (block.Type)
        {
            case BlockType.user_message:
                if (s.wasReasoning || s.wasTool || s.wasText || !s.lineStart)
                {
                    if (!s.lineStart) Console.WriteLine();
                    Console.WriteLine();
                }
                Console.ForegroundColor = UserColor;
                Console.WriteLine($"> {block.Content}");
                Console.ResetColor();
                Console.WriteLine();
                s.wasTool = s.wasReasoning = s.wasText = false;
                s.lineStart = true;
                break;
            case BlockType.agent_task:
                if (s.wasReasoning || s.wasTool || s.wasText || !s.lineStart)
                {
                    if (!s.lineStart) Console.WriteLine();
                    Console.WriteLine();
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"> {block.Content}");
                Console.ResetColor();
                Console.WriteLine();
                s.wasTool = s.wasReasoning = s.wasText = false;
                s.lineStart = true;
                break;
            case BlockType.agent_message:
                if ((s.wasReasoning || s.wasTool) && !s.lineStart)
                {
                    Console.WriteLine();
                    s.lineStart = true;
                }
                Console.ForegroundColor = AgentColor;
                var formatted = TableRenderer.RenderTables(block.Content);
                Console.Write(formatted);
                Console.ResetColor();
                s.wasReasoning = false;
                s.wasTool = false;
                s.wasText = true;
                s.lineStart = false;
                break;
            case BlockType.agent_reasoning:
                if (s.wasTool || s.wasText)
                {
                    if (!s.lineStart) Console.WriteLine();
                    Console.WriteLine();
                    s.lineStart = true;
                }
                Console.ForegroundColor = ReasoningColor;
                Console.Write(block.Content);
                Console.ResetColor();
                s.wasReasoning = true;
                s.wasTool = false;
                s.wasText = false;
                s.lineStart = false;
                break;
            case BlockType.auto_tool:
                if ((s.wasReasoning || s.wasTool || s.wasText) && !s.lineStart)
                {
                    Console.WriteLine();
                    s.lineStart = true;
                }
                if (s.wasReasoning || s.wasTool || s.wasText)
                {
                    Console.WriteLine();
                    s.lineStart = true;
                }
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[AutoTool: {block.ToolName} | {block.Content}]");
                if (block.ToolResult is not null)
                    Console.WriteLine(block.ToolResult);
                Console.ResetColor();
                s.wasTool = true;
                break;
            case BlockType.tool:
                s.lastToolName = block.ToolName ?? "";
                s.toolNameQueue.Enqueue(s.lastToolName);
                if ((s.wasReasoning || s.wasTool || s.wasText) && !s.lineStart)
                {
                    Console.WriteLine();
                    s.lineStart = true;
                }
                if (s.wasReasoning || s.wasTool || s.wasText)
                {
                    Console.WriteLine();
                    s.lineStart = true;
                }
                var toolColor = GetToolColor(block.ToolName, block.Content);
                Console.ForegroundColor = toolColor;
                var displayContent = MaskContentArgs(block.ToolName, block.Content);
                Console.WriteLine($"[Tool: {block.ToolName} | {displayContent}]");
                Console.ResetColor();
                s.wasTool = true;
                s.wasReasoning = false;
                s.wasText = false;
                s.lineStart = true;

                // Render ToolResult if present (new system — result stored in tool block)
                if (block.ToolResult is not null)
                {
                    var trLen = block.ToolName is not null
                        ? _streamOpts.GetMaxLength(block.ToolName, -1)
                        : -1;
                    if (trLen == 0) { /* hidden */ }
                    else
                    {
                        Console.ForegroundColor = ToolResultColor;
                        var trContent = block.ToolResult.TrimEnd('\n', '\r');
                        if (trLen > 0 && trContent.Length > trLen)
                            trContent = trContent[..trLen];
                        // Format any markdown tables in tool results (subagents may return tables)
                        trContent = TableRenderer.RenderTables(trContent);
                        Console.WriteLine(trContent);
                        Console.ResetColor();
                    }
                }
                break;
            case BlockType.system_info:
                if (s.wasReasoning || s.wasTool || s.wasText || !s.lineStart)
                {
                    if (!s.lineStart) Console.WriteLine();
                    Console.WriteLine();
                }
                Console.ForegroundColor = UserColor;
                Console.WriteLine($"[!] {block.Content}");
                Console.ResetColor();
                s.wasTool = s.wasReasoning = s.wasText = false;
                s.lineStart = true;
                break;
        }
    }

    public async Task ReplayBlocksAsync(string sid, IBlockStore store, bool showResumed = true)
    {
        await RefreshAsync(sid);
        if (showResumed) Console.WriteLine($"Resumed agent '{sid}'.");
        var prevBlocks = await store.LoadBlocksAsync(sid);

        // Show only the last 2 turns: find the second-to-last turn marker
        // and render everything from that point onwards.
        var turnBlocks = prevBlocks
            .Where(b => b.Type == BlockType.turn)
            .OrderByDescending(b => b.Number)
            .Take(2)
            .ToArray();

        if (turnBlocks.Length == 2)
        {
            var cutNumber = turnBlocks[1].Number;
            prevBlocks = prevBlocks
                .Where(b => b.Number >= cutNumber)
                .ToList();
        }

        var s = new RenderState();
        foreach (var block in prevBlocks)
            RenderBlock(block, ref s);
        Console.WriteLine();
        Console.WriteLine();
    }

    private ConsoleColor GetToolColor(string? toolName, string args)
    {
        // Subagent tools
        if (toolName == "subagent_run")
            return ConsoleColor.DarkMagenta;
        if (toolName == "subagent_use")
            return ConsoleColor.Magenta;

        // read_file → Green
        if (toolName == "read_file")
            return ConsoleColor.Green;

        // patch_file & write_file → Blue
        if (toolName is "patch_file" or "write_file")
            return ConsoleColor.Blue;

        // bash → DarkCyan
        if (toolName == "bash")
            return ConsoleColor.DarkCyan;

        // Memory tool color depends on action
        if (toolName == "memory")
        {
            try
            {
                using var doc = JsonDocument.Parse(args);
                var action = doc.RootElement.GetProperty("action").GetString();
                return action switch
                {
                    "delete" or "clean" or "recover" => ConsoleColor.Yellow,
                    _ => ToolCallColor // stats, list → unchanged
                };
            }
            catch
            {
                return ToolCallColor;
            }
        }

        return ToolCallColor;
    }

    private string MaskContentArgs(string? toolName, string content)
    {
        if (toolName is null) return content;

        var hidden = _streamOpts.ToolHiddenArgs.GetValueOrDefault(toolName);
        var hasHidden = hidden is not null && hidden.Length > 0;
        var hasCwd = AgentCwd is not null;

        // Skip JSON parsing if nothing to do
        if (!hasHidden && !hasCwd)
            return content;

        try
        {
            var json = JsonNode.Parse(content);
            if (json is not JsonObject obj) return content;

            // Hide sensitive args
            if (hasHidden)
            {
                foreach (var arg in hidden!)
                {
                    if (obj.ContainsKey(arg))
                        obj[arg] = "***";
                }
            }

            // Replace agent cwd prefix in path/workdir/cwd args with {cwd}
            if (hasCwd)
            {
                var cwd = AgentCwd!.Replace('\\', '/').TrimEnd('/');
                var cwdPrefix = cwd + "/";

                void ReplacePathArg(string argName)
                {
                    if (obj.TryGetPropertyValue(argName, out var val) &&
                        val is JsonValue jv && jv.TryGetValue<string>(out var str))
                    {
                        var normalized = str.Replace('\\', '/');
                        if (normalized == cwd)
                            obj[argName] = "{cwd}";
                        else if (normalized.StartsWith(cwdPrefix, StringComparison.OrdinalIgnoreCase))
                            obj[argName] = $"{{cwd}}/{normalized[cwdPrefix.Length..]}";
                    }
                }

                ReplacePathArg("path");
                ReplacePathArg("workdir");
                ReplacePathArg("cwd");
            }

            return obj.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch
        {
            return content;
        }
    }

    public static string FormatTokenCount(long count) => count >= 1000
        ? $"{count / 1000.0:F1}K"
        : count.ToString();

    public void RenderStats(int totalBlocks, Dictionary<string, int> typeStats, string? model,
        long cumHit, long cumMiss, long cumOutput, double totalCost)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("── Stats ──────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var kv in typeStats)
        {
            if (kv.Key is "system_info" or "agent_data") continue;
            var icon = Glyphite.Host.Utils.BlockTypeIcon.Get(kv.Key);
            var label = icon == "  " ? kv.Key : $"{icon} {kv.Key}";
            Console.WriteLine($"  {label,-22}: {kv.Value,4}");
        }
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ───────────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  Blocks:    {totalBlocks}");
        if (model is not null)
            Console.WriteLine($"  Model:     {model}");

        if (cumHit + cumMiss + cumOutput > 0)
        {
            Console.WriteLine($"  Input:     {FormatTokenCount(cumHit + cumMiss)}");
            Console.WriteLine($"  Output:    {FormatTokenCount(cumOutput)}");
            var cumRate = (int)(cumHit * 100.0 / (cumHit + cumMiss + cumOutput));
            Console.WriteLine($"  Cache:     {FormatTokenCount(cumHit)} hit / {FormatTokenCount(cumMiss)} miss ({cumRate}%)");
            var costStr = totalCost > 0 ? $"${totalCost / 1_000_000.0:F2}" : "$?";
            Console.WriteLine($"  Cost:      {costStr}");
        }

        Console.ResetColor();
        Console.WriteLine();
    }
}

public record struct RenderState
{
    public string? lastToolName;
    public Queue<string> toolNameQueue = new();
    public bool wasTool;
    public bool wasText;
    public bool wasReasoning;
    public bool lineStart = true;
    public RenderState() { }
}