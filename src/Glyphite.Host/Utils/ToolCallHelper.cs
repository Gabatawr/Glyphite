using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Utils;

public static class ToolCallHelper
{
    /// <summary>
    /// Determine if a tool call should be treated as peek (auto-clean after LLM consumes it).
    /// Default: peek=true for patch_file; otherwise checks the 'peek' argument.
    /// </summary>
    public static bool IsPeekCall(FunctionCallContent fcc)
    {
        return IsPeekCall(fcc.Name, fcc.Arguments);
    }

    /// <summary>
    /// Determine if a tool call should be treated as peek.
    /// Default: peek=true for patch_file; otherwise checks the 'peek' argument.
    /// </summary>
    public static bool IsPeekCall(string? toolName, IDictionary<string, object?>? args)
    {
        if (args?.TryGetValue("peek", out var pv) == true)
            return pv is bool pb ? pb : (pv is JsonElement je && je.ValueKind == JsonValueKind.True);
        return toolName == "patch_file";
    }

    /// <summary>
    /// Remove ChatMessages from a list by matching their Text against block number prefixes.
    /// Used after `memory clean` to remove deleted blocks from in-memory message lists.
    /// </summary>
    public static void RemoveBlocksFromMessageList(IList<ChatMessage> messages, IEnumerable<double> blockNumbers)
    {
        foreach (var num in blockNumbers)
        {
            var pat = $"[Block: {num:F1},";
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Text?.StartsWith(pat) == true)
                {
                    messages.RemoveAt(i);
                    break;
                }
            }
        }
    }
}