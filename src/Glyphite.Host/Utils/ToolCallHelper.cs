using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Utils;

public static class ToolCallHelper
{
    /// <summary>
    /// Determine if a tool call should be treated as peek (auto-clean after LLM consumes it).
    /// Default: peek=true for write_file and patch_file; otherwise checks the 'peek' argument.
    /// </summary>
    public static bool IsPeekCall(FunctionCallContent fcc)
    {
        return IsPeekCall(fcc.Name, fcc.Arguments);
    }

    /// <summary>
    /// Determine if a tool call should be treated as peek.
    /// Default: peek=true for write_file and patch_file; otherwise checks the 'peek' argument.
    /// </summary>
    public static bool IsPeekCall(string? toolName, IDictionary<string, object?>? args)
    {
        if (args?.TryGetValue("peek", out var pv) == true)
            return pv is bool pb ? pb : (pv is JsonElement je && je.ValueKind == JsonValueKind.True);
        return toolName == "patch_file" || toolName == "write_file";
    }

    /// <summary>Format a number to K notation: &lt;1000 raw, &gt;=1000 as X.YK (e.g. 133.2K).</summary>
    public static string FormatK(long val) => val < 1000 ? val.ToString("N0") : $"{val / 1000.0:F1}K";
}