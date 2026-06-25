using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Cli.Services;
using Glyphite.Host.Data;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private async Task<bool> HandleCommandAsync(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0];
        var arg = parts.Length > 1 ? parts[1] : null;

        // Multiple words after command → treat as if no argument
        if (arg is not null && arg.Contains(' '))
            arg = null;

        switch (cmd)
        {
            case "/new":
                var r = await _session.HandleNewCommandAsync(arg);
                var usage = await _session.ResetSessionStateAsync();
                (_lastTurnHit, _lastTurnMiss, _lastTurnOutput, _lastTurnLastHit, _lastTurnLastMiss) = usage;
                await UpdatePromptPrefixAsync();
                return r;

            case "/clone":
                return await _session.HandleCloneCommandAsync(arg);

            case "/use":
                return await _session.HandleUseCommandAsync(arg);

            case "/delete":
                return await _session.HandleDeleteCommandAsync(arg);

            case "/stats":
                var (totalBlocks, _, typeStats) = await BlockMemory.ComputeStatsAsync(AgentId!);
                if (totalBlocks == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("No stats available yet.");
                    Console.ResetColor();
                    Console.WriteLine();
                    return true;
                }

                var statsModel = await BlockMemory.GetAgentModelAsync(AgentId!);
                var (cumHit, cumMiss, cumOutput) = await _agentStore.GetUsageAsync(AgentId!);

                var totalCost = 0.0;
                if (cumHit + cumMiss + cumOutput > 0)
                {
                    var usageByModel = await _agentStore.GetUsageByModelAsync(AgentId!);
                    foreach (var (modelName, hit, miss, output) in usageByModel)
                    {
                        var (mPrice, hPrice, oPrice) = GetPricing(modelName);
                        if (mPrice.HasValue)
                            totalCost += miss * mPrice.Value + (hPrice ?? 0) * hit + (oPrice ?? mPrice.Value) * output;
                    }
                }

                _renderer.RenderStats(totalBlocks, typeStats, statsModel, cumHit, cumMiss, cumOutput, totalCost);
                return true;

            case "/version":
                var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
                if (ver is not null)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"Glyphite v{ver.Major}.{ver.Minor}.{ver.Build}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Version not available.");
                    Console.ResetColor();
                }
                Console.WriteLine();
                return true;

            case "/models":
                await _session.ShowModelSelectionAsync();
                return true;

            case "/compression":
                var llmOpts = await _cfgService.GetOptionsAsync<LlmOptions>(LlmOptions.Section, AgentId);
                var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, AgentId);
                var strategy = CompactionService.PickStrategy(compOpts);

                // Determine mode via zone classification (to show user what will happen)
                var blocks = await _blockStore.LoadBlocksAsync(AgentId);
                var turnGroups = CompactionService.GroupByTurns(blocks);
                var threshold = (int)(compOpts.AutoThreshold / 100.0 * llmOpts.ContextWindow);
                var zoneClass = CompactionService.ClassifyZones(turnGroups, threshold);

                if (zoneClass.ToCompressGroups.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine("Compaction not needed — all eligible zones are already compressed.");
                    Console.ResetColor();
                    Console.WriteLine();
                    return true;
                }

                var mode = zoneClass.IsSafeHardMode ? "safe+" : "";
                mode += zoneClass.IsHardMode ? "hard" : "soft";

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"Running {strategy} ({mode}) compaction...");
                Console.ResetColor();

                var compacted = await _compactionService.CompactAsync(AgentId, llmOpts.ContextWindow, strategy);

                Console.ForegroundColor = compacted ? ConsoleColor.Green : ConsoleColor.DarkYellow;
                Console.WriteLine(compacted ? "Compaction complete." : "Compaction did not run.");
                Console.ResetColor();
                Console.WriteLine();
                return true;
        }

        return false;
    }

    /// <summary>Monitor for Escape key to cancel a running turn.</summary>
    private static async Task MonitorEscapeAsync(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape)
                    cts.Cancel();
            }
            else
            {
                try
                {
                    await Task.Delay(100, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
