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

    private readonly ToolStreamingOptions _streamOpts;

    public ConsoleRenderer(ToolStreamingOptions streamOpts)
    {
        _streamOpts = streamOpts;
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
            case BlockType.agent_message:
                if ((s.wasReasoning || s.wasTool) && !s.lineStart)
                {
                    Console.WriteLine();
                    s.lineStart = true;
                }
                Console.ForegroundColor = AgentColor;
                Console.Write(block.Content);
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
                    var trLen = -1;
                    if (block.ToolName is not null)
                        _streamOpts.ToolMaxLength.TryGetValue(block.ToolName, out trLen);
                    if (trLen == 0) { /* hidden */ }
                    else
                    {
                        Console.ForegroundColor = ToolResultColor;
                        var trContent = block.ToolResult.TrimEnd('\n', '\r');
                        if (trLen > 0 && trContent.Length > trLen)
                            trContent = trContent[..trLen];
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

    public async Task ReplayBlocksAsync(string sid, IMemoryStore store, bool showResumed = true)
    {
        if (showResumed) Console.WriteLine($"Resumed agent '{sid}'.");
        var prevBlocks = await store.LoadBlocksAsync(sid);
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
        if (hidden is null || hidden.Length == 0) return content;

        try
        {
            var json = JsonNode.Parse(content);
            if (json is not JsonObject obj) return content;

            foreach (var arg in hidden)
            {
                if (obj.ContainsKey(arg))
                    obj[arg] = "***";
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