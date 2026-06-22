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
        bool? cascade,
        IBlockMemoryProvider provider,
        string sessionId,
        IConfigService? cfg = null)
    {
        switch (action.ToLowerInvariant())
        {
            case "delete":
            case "clean":
                if (blocks is null || blocks.Length == 0)
                    return "No block numbers provided for deletion.";
                return await provider.DeleteBlocksAsync(sessionId, blocks, cascade ?? true);

            case "recover":
                if (blocks is null || blocks.Length == 0)
                    return "No block numbers provided for recovery.";
                var recovered = await provider.RecoverBlocksAsync(sessionId, blocks, cascade ?? false);
                return $"Recovered {recovered} block{(recovered == 1 ? "" : "s")}.";

            case "list":
            {
                var allBlocks = await provider.GetBlocksAsync(sessionId);
                var protectedSet = cfg is not null
                    ? (await cfg.GetOptionsAsync<MemoryOptions>(MemoryOptions.Section, sessionId)).ProtectedBlockTypes
                    : [];
                var protectedTypes = new HashSet<string>(protectedSet, StringComparer.OrdinalIgnoreCase);
                var blockLines = new List<string> { "── Memory Blocks ────────────────────────" };
                foreach (var block in allBlocks.OrderBy(b => b.Number))
                {
                    var isProtected = protectedTypes.Contains(block.Type.ToString());
                    var typeDisplay = block.Type.ToString();
                    if (block.ToolName is not null)
                        typeDisplay += $"/{block.ToolName}";
                    var prefix = isProtected ? "[!]" : "   ";
                    var preview = (block.Content ?? "").Replace('\n', ' ').Replace('\r', ' ');
                    if (preview.Length > 64)
                        preview = preview[..64] + "...";
                    else if (preview.Length == 0)
                        preview = "(empty)";
                    blockLines.Add($"  {prefix} {block.Number,5:F1} {typeDisplay,-22} {preview}");
                }
                blockLines.Add("  ───────────────────────────────────────");
                blockLines.Add($"  Total: {allBlocks.Count}");
                return string.Join("\n", blockLines);
            }

            case "stats":
                var (totalBlocks, _, typeStats) = await provider.ComputeStatsAsync(sessionId);
                var lines = new List<string> { "── Memory Stats ─────────────────────────" };
                foreach (var kv in typeStats.OrderByDescending(kv => kv.Value))
                {
                    var icon = BlockTypeIcon.Get(kv.Key);
                    lines.Add($"  {icon} {kv.Key,-20}: {kv.Value,4}");
                }
                lines.Add("  ───────────────────────────────────────");
                lines.Add($"  Blocks: {totalBlocks}");

                // Model
                var model = await provider.GetAgentModelAsync(sessionId);
                if (model is not null)
                    lines.Add($"  Model:  {model}");

                // Real API usage + pricing
                if (cfg is not null)
                {
                    var usage = await provider.GetUsageAsync(sessionId);
                    if (usage.Hit + usage.Miss + usage.Output > 0)
                    {
                        lines.Add($"  Input:  {(usage.Hit + usage.Miss) / 1000.0:F1}K");
                        lines.Add($"  Output: {usage.Output / 1000.0:F1}K");
                        var rate = (int)(usage.Hit * 100.0 / (usage.Hit + usage.Miss + usage.Output));
                        lines.Add($"  Cache:  {usage.Hit / 1000.0:F1}K hit / {usage.Miss / 1000.0:F1}K miss ({rate}%)");
                    }

                    var deepseekOpts = await cfg.GetOptionsAsync<DeepSeekOptions>(DeepSeekOptions.Section);
                    var modelPricing = deepseekOpts.Models.FirstOrDefault(m =>
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
                return $"Unknown action '{action}'. Use 'stats', 'list', 'clean', or 'recover'.";
        }
    }

    private sealed class Invoker(IBlockMemoryProvider provider, string sessionId, IConfigService? cfg)
    {
        [Description("Memory management tool. Actions: 'stats' — show block type distribution, token usage, cache stats, and cost; 'list' — show all blocks with numbers, types, and content previews (protected blocks marked [!]); 'clean' — soft-delete blocks by number (remove clutter from context); 'recover' — restore soft-deleted blocks by number.")]
        public Task<string> Execute(
            [Description("Action: 'stats' (show memory stats), 'list' (list all blocks with numbers and previews), 'clean' (remove blocks to free context), 'recover' (restore cleaned blocks)")] string action,
            [Description("Block numbers for clean/recover, e.g. [5.0, 7.0, 9.0]. Not needed for 'stats' or 'list'.")] double[]? blocks = null,
            [Description("Cascade along parent chain: true=follow Data['parentNumber']. Default: true for clean, false for recover.")] bool? cascade = null,
            bool? peek = true)
            => MemoryTool.Execute(action, blocks, cascade, provider, sessionId, cfg);
    }

    public static AIFunction AsAIFunction(IBlockMemoryProvider provider, string sessionId, IConfigService? cfg = null)
        => AIFunctionFactory.Create(
            new Invoker(provider, sessionId, cfg).Execute,
            "memory");
}
