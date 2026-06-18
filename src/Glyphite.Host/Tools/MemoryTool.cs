using System.ComponentModel;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Memory;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class MemoryTool
{
    [Description("Memory management tool. Actions: 'stats' — show block type distribution and token usage; 'delete' — soft-delete blocks by number (cascade=true follows parent chain); 'recover' — restore soft-deleted blocks by number. Cascade defaults: true for delete, false for recover.")]
    public static async Task<string> Execute(
        [Description("Action to perform: 'stats' (show memory statistics), 'delete' (soft-delete blocks by number), 'recover' (restore soft-deleted blocks by number)")] string action,
        [Description("Block numbers for delete/recover actions, e.g. [5.0, 7.0, 9.0]. Not needed for 'stats' action.")] double[]? blocks,
        [Description("Cascade along parent chain: true=follow Data['parentNumber'], false=exact blocks only. Default: true for delete, false for recover.")] bool? cascade,
        IBlockMemoryProvider provider,
        string sessionId)
    {
        switch (action.ToLowerInvariant())
        {
            case "delete":
                if (blocks is null || blocks.Length == 0)
                    return "No block numbers provided for deletion.";
                return await provider.DeleteBlocksAsync(sessionId, blocks, cascade ?? true);

            case "recover":
                if (blocks is null || blocks.Length == 0)
                    return "No block numbers provided for recovery.";
                var recovered = await provider.RecoverBlocksAsync(sessionId, blocks, cascade ?? false);
                return $"Recovered {recovered} block{(recovered == 1 ? "" : "s")}.";

            case "stats":
                var (totalBlocks, totalTokens, typeStats) = await provider.ComputeStatsAsync(sessionId);
                var lines = new List<string> { "── Memory Stats ─────────────────────────" };
                var iconMap = new Dictionary<string, string>
                {
                    ["user_message"] = "👤", ["agent_message"] = "💬", ["agent_reasoning"] = "🧠",
                    ["tool"] = "🔧", ["todo"] = "📋", ["todo_update"] = "🔄",
                    ["system_info"] = "ℹ️"
                };
                foreach (var kv in typeStats.OrderByDescending(kv => kv.Value))
                {
                    var icon = iconMap.GetValueOrDefault(kv.Key, "  ");
                    lines.Add($"  {icon} {kv.Key,-20}: {kv.Value,4}");
                }
                lines.Add("  ───────────────────────────────────────");
                lines.Add($"  Blocks: {totalBlocks}");
                lines.Add($"  Tokens: {totalTokens / 1000.0:F1}K");
                return string.Join("\n", lines);

            default:
                return $"Unknown action '{action}'. Use 'stats', 'delete', or 'recover'.";
        }
    }

    public static AIFunction AsAIFunction(IBlockMemoryProvider provider, string sessionId)
        => AIFunctionFactory.Create(
            (string action, double[]? blocks = null, bool? cascade = null) => Execute(action, blocks, cascade, provider, sessionId),
            "memory");
}