using System.Text.Json;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Utils;

public static class ToolCallHelper
{
    /// <summary>
    /// Determine if a tool call should be treated as peek (auto-clean after LLM consumes it).
    /// Checks <c>extra_cfg.peek</c> first (from LLM), then falls back to the tool's
    /// <see cref="ToolConfigDecorator.DefaultPeek"/> if a decorator is provided, then
    /// falls back to the legacy flat <c>peek</c> argument.
    /// </summary>
    public static bool IsPeekCall(FunctionCallContent fcc)
    {
        return IsPeekCall(fcc.Name, fcc.Arguments);
    }

    /// <summary>
    /// Determine peek status with <see cref="ToolConfigDecorator"/> fallback.
    /// Call this when you have access to the tool instance.
    /// </summary>
    public static bool IsPeekCall(AIFunction? tool, string? toolName, IDictionary<string, object?>? args)
    {
        // 1. Check for explicit extra_cfg.peek from the LLM
        if (args is not null && TryGetExtraCfgPeek(args, out var extraCfgPeek))
            return extraCfgPeek;

        // 2. Check legacy flat 'peek' argument (old LLMs)
        if (args?.TryGetValue("peek", out var pv) == true)
            return pv is bool pb ? pb : (pv is JsonElement je && je.ValueKind == JsonValueKind.True);

        // 3. Fall back to ToolConfigDecorator.DefaultPeek
        if (tool is ToolConfigDecorator decorator)
            return decorator.DefaultPeek;

        // 4. Legacy default: write_file and patch_file are peek by default
        return toolName == "patch_file" || toolName == "write_file";
    }

    /// <summary>
    /// Determine if a tool call should be treated as peek (legacy path).
    /// Checks <c>extra_cfg.peek</c> first, then flat <c>peek</c>, then tool name defaults.
    /// </summary>
    public static bool IsPeekCall(string? toolName, IDictionary<string, object?>? args)
    {
        return IsPeekCall(null, toolName, args);
    }

    /// <summary>Extracts <c>peek</c> from <c>extra_cfg</c> object if present.</summary>
    private static bool TryGetExtraCfgPeek(IDictionary<string, object?> args, out bool peek)
    {
        peek = false;
        if (!args.TryGetValue("extra_cfg", out var cfgObj) || cfgObj is null)
            return false;

        if (cfgObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("peek", out var p) && p.ValueKind == JsonValueKind.True) { peek = true; return true; }
            if (je.TryGetProperty("peek", out p) && p.ValueKind == JsonValueKind.False) { peek = false; return true; }
        }
        else if (cfgObj is IReadOnlyDictionary<string, object?> cfgDict)
        {
            if (cfgDict.TryGetValue("peek", out var p) && p is bool pb) { peek = pb; return true; }
            if (cfgDict.TryGetValue("peek", out p) && p is JsonElement pje)
            {
                if (pje.ValueKind == JsonValueKind.True) { peek = true; return true; }
                if (pje.ValueKind == JsonValueKind.False) { peek = false; return true; }
            }
        }

        return false;
    }

    /// <summary>Format a number to K notation: &lt;1000 raw, &gt;=1000 as X.YK (e.g. 133.2K).</summary>
    public static string FormatK(long val) => val < 1000 ? val.ToString("N0") : $"{val / 1000.0:F1}K";
}
