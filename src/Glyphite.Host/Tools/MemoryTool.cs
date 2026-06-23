using System.ComponentModel;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Memory;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class MemoryTool
{
    public static async Task<string> Execute(
        string action,
        double[]? blocks,
        IBlockMemoryProvider provider,
        string agentId,
        IConfigService? cfg = null)
    {
        switch (action.ToLowerInvariant())
        {
            case "stats":
                var (totalBlocks, _, typeStats) = await provider.ComputeStatsAsync(agentId);
                var lines = new List<string> { "── Memory Stats ─────────────────────────" };
                foreach (var kv in typeStats.OrderByDescending(kv => kv.Value))
                {
                    var icon = BlockTypeIcon.Get(kv.Key);
                    lines.Add($"  {icon} {kv.Key,-20}: {kv.Value,4}");
                }
                lines.Add("  ───────────────────────────────────────");
                lines.Add($"  Blocks: {totalBlocks}");

                // Model
                var model = await provider.GetAgentModelAsync(agentId);
                if (model is not null)
                    lines.Add($"  Model:  {model}");

                // Real API usage + pricing
                if (cfg is not null)
                {
                    var usage = await provider.GetUsageAsync(agentId);
                    if (usage.Hit + usage.Miss + usage.Output > 0)
                    {
                        lines.Add($"  Input:  {(usage.Hit + usage.Miss) / 1000.0:F1}K");
                        lines.Add($"  Output: {usage.Output / 1000.0:F1}K");
                        var rate = (int)(usage.Hit * 100.0 / (usage.Hit + usage.Miss + usage.Output));
                        lines.Add($"  Cache:  {usage.Hit / 1000.0:F1}K hit / {usage.Miss / 1000.0:F1}K miss ({rate}%)");
                    }

                    var llmOpts = await cfg.GetOptionsAsync<LlmOptions>(LlmOptions.Section);
                    var modelPricing = llmOpts.Models.FirstOrDefault(m =>
                        string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
                    if (modelPricing is not null)
                    {
                        var cost = (usage.Miss * modelPricing.Miss + usage.Hit * modelPricing.Hit + usage.Output * modelPricing.Output) / 1_000_000.0;
                        if (cost >= 0.01)
                            lines.Add($"  Cost:   ${cost:F2}");
                        else
                            lines.Add($"  Cost:   ${cost:F6}");
                    }
                }

                return string.Join("\n", lines);

            default:
                return $"Unknown action '{action}'. Use 'stats'.";
        }
    }

    private sealed class Invoker(IBlockMemoryProvider provider, string agentId, IConfigService? cfg)
    {
        [Description("Memory management tool. Actions: 'stats' — show block type distribution, token usage, cache stats, and cost.")]
        public Task<string> Execute(
            [Description("Action: 'stats' (show memory stats)")] string action,
            [Description("Not used.")] double[]? blocks = null,
            bool? peek = true)
            => MemoryTool.Execute(action, blocks, provider, agentId, cfg);
    }

    public static AIFunction AsAIFunction(IBlockMemoryProvider provider, string agentId, IConfigService? cfg = null)
        => AIFunctionFactory.Create(
            new Invoker(provider, agentId, cfg).Execute,
            "memory");
}
