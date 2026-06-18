using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Cli.Services;
using Glyphite.Host.Data;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Memory;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;
namespace Glyphite.Cli;

public partial class ChatRepl
{
    private async Task<bool> HandleCommandAsync(string input)
    {
        switch (input)
        {
            case "/new":    return await HandleNewCommandAsync();
            case "/clone":  return await HandleCloneCommandAsync();
            case "/use":    return await HandleUseCommandAsync();

            case "/reload":
                Console.Clear();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("========= RELOAD =========");
                Console.ResetColor();
                Console.WriteLine();
                await _renderer.ReplayBlocksAsync(_agentId!, _store, showResumed: false);
                return true;

            case "/stats":
                var (totalBlocks, _, typeStats) = await _blockMemory.ComputeStatsAsync(_agentId!);
                if (totalBlocks == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("No stats available yet.");
                    Console.ResetColor();
                    Console.WriteLine();
                    return true;
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("── Stats ──────────────────────────────");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkGray;
                foreach (var kv in typeStats)
                {
                    if (kv.Key is "system_info" or "agent_data") continue;
                    var label = kv.Key switch
                    {
                        "user_message" => "👤 user_message",
                        "agent_message" => "💬 agent_message",
                        "agent_reasoning" => "🧠 agent_reasoning",
                        "tool" => "🔧 tool",
                        "auto_tool" => "🤖 auto_tool",
                        "file" => "📄 file",
                        "todo" or "todo_update" => "📋 todo",
                        _ => kv.Key
                    };
                    Console.WriteLine($"  {label,-22}: {kv.Value,4}");
                }
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  ───────────────────────────────────");
                Console.ResetColor();

                // Model
                var statsModel = await _blockMemory.GetAgentModelAsync(_agentId!);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"  Blocks:    {totalBlocks}");
                if (statsModel is not null)
                    Console.WriteLine($"  Model:     {statsModel}");

                // Real API usage
                var (cumHit, cumMiss, cumOutput) = await _store.GetUsageAsync(_agentId!);
                if (cumHit + cumMiss + cumOutput > 0)
                {
                    Console.WriteLine($"  Input:     {ConsoleRenderer.FormatTokenCount(cumHit + cumMiss)}");
                    Console.WriteLine($"  Output:    {ConsoleRenderer.FormatTokenCount(cumOutput)}");
                    var cumRate = (int)(cumHit * 100.0 / (cumHit + cumMiss + cumOutput));
                    Console.WriteLine($"  Cache:     {ConsoleRenderer.FormatTokenCount(cumHit)} hit / {ConsoleRenderer.FormatTokenCount(cumMiss)} miss ({cumRate}%)");

                    var (mPrice, hPrice, oPrice) = GetPricing(statsModel ?? _deepseek.Model);
                    if (mPrice.HasValue)
                    {
                        var totalCost = (cumMiss * mPrice.Value + cumHit * (hPrice ?? 0) + cumOutput * (oPrice ?? mPrice.Value)) / 1_000_000.0;
                        Console.WriteLine($"  Cost:      ${totalCost:F2}");
                    }
                }

                Console.ResetColor();
                Console.WriteLine();
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
                await ShowModelSelectionAsync();
                return true;
        }

        return false;
    }
    private async Task<bool> HandleNewCommandAsync()
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write("Enter new agent name: ");
        Console.ResetColor();
        var name = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Cancelled.\n");
            Console.ResetColor();
            return true;
        }

        if (!AgentManager.IsValidAgentName(name))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid agent name '{name}'. Use letters, digits, hyphens, underscores (max 100 chars).\n");
            Console.ResetColor();
            return true;
        }

        var cwd = Directory.GetCurrentDirectory();

        if (await _store.AgentExistsAsync(name))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"Agent '{name}' already exists. Delete and recreate? (y/N): ");
            Console.ResetColor();
            var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (confirm is "y" or "yes")
            {
                await _store.DeleteSessionAsync(name);
                _agentId = await _agentManager.CreateAgentAsync(name, _deepseek.Model, cwd);
                _agentOpts.AgentName = name;
                await LoadAgentConfigAsync(_agentId, cwd);
                await ResetSessionStateAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Agent '{name}' recreated.\n");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Cancelled.\n");
                Console.ResetColor();
            }
            return true;
        }

        _agentId = await _agentManager.CreateAgentAsync(name, _deepseek.Model, cwd);
        _agentOpts.AgentName = name;
        await LoadAgentConfigAsync(_agentId, cwd);
        await ResetSessionStateAsync();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent '{name}' created.\n");
        Console.ResetColor();
        return true;
    }

    private async Task<bool> HandleCloneCommandAsync()
    {
        var sessions = await _store.ListAgentsAsync();
        if (sessions.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No agents found. Create one with /new first.\n");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("Clone copies an agent's history to a new name.\n");

        Console.WriteLine("Pick source agent to copy from:");
        for (int i = 0; i < sessions.Count; i++)
        {
            var isCurrent = string.Equals(sessions[i], _agentId, StringComparison.Ordinal);
            var blockCount = await _store.GetBlockCountAsync(sessions[i]);
            var marker = isCurrent ? " ← current" : "";
            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {sessions[i]} ({blockCount} blocks){marker}");
        }

        Console.ResetColor();
        Console.Write($"\nSelect source [1, default=current]: ");
        var selection = Console.ReadLine()?.Trim();

        string sourceName;
        if (string.IsNullOrEmpty(selection))
        {
            sourceName = _agentId;
        }
        else if (int.TryParse(selection, out var idx) && idx >= 1 && idx <= sessions.Count)
        {
            sourceName = sessions[idx - 1];
        }
        else
        {
            if (!sessions.Contains(selection))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Agent '{selection}' not found.\n");
                Console.ResetColor();
                return true;
            }
            sourceName = selection;
        }

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write($"\nEnter new agent name (clone of '{sourceName}'): ");
        Console.ResetColor();
        var cloneName = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(cloneName))
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("Cancelled.\n");
            Console.ResetColor();
            return true;
        }

        if (!AgentManager.IsValidAgentName(cloneName))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid agent name '{cloneName}'. Use letters, digits, hyphens, underscores (max 100 chars).\n");
            Console.ResetColor();
            return true;
        }

        if (await _store.AgentExistsAsync(cloneName))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{cloneName}' already exists. Choose a different name.\n");
            Console.ResetColor();
            return true;
        }

        var cwd = Directory.GetCurrentDirectory();
        await _store.ForkSessionAsync(sourceName, cloneName, cwd);
        _agentId = cloneName;
        _agentOpts.AgentName = cloneName;
        await LoadAgentConfigAsync(_agentId, cwd);
        await ResetSessionStateAsync();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent '{cloneName}' cloned from '{sourceName}'.\n");
        Console.ResetColor();
        return true;
    }

    private async Task<bool> HandleUseCommandAsync()
    {
        var sessions = await _store.ListAgentsAsync();
        if (sessions.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No agents found. Create one with /new first.\n");
            Console.ResetColor();
            return true;
        }

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("Select agent to use:\n");
        Console.ResetColor();

        var cwd = Directory.GetCurrentDirectory();
        for (int i = 0; i < sessions.Count; i++)
        {
            var isCurrent = string.Equals(sessions[i], _agentId, StringComparison.Ordinal);
            var agentHome = await _store.GetAgentHomePathAsync(sessions[i]) ?? "";
            var createdAt = await _store.GetAgentCreatedAtAsync(sessions[i]) ?? "";
            var blockCount = await _store.GetBlockCountAsync(sessions[i]);
            var lastLaunch = await _store.GetLastLaunchPathAsync(sessions[i]);

            var marker = isCurrent ? " ← current" : "";
            var atHome = (string.Equals(agentHome, cwd, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(agentHome)) ? " [🏠 home]" : "";
            var createdDate = createdAt.Length >= 10 ? createdAt[..10] : "";
            var lastStr = lastLaunch is not null ? $" last: {lastLaunch}" : "";

            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {sessions[i]} ({blockCount} blocks, {createdDate}){atHome}{lastStr}{marker}");
        }

        Console.ResetColor();
        Console.Write($"\nSelect agent [1-{sessions.Count}, default=cancel]: ");
        var pick = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(pick))
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("Cancelled.\n");
            Console.ResetColor();
            return true;
        }

        string targetName;
        if (int.TryParse(pick, out var idx) && idx >= 1 && idx <= sessions.Count)
        {
            targetName = sessions[idx - 1];
        }
        else if (sessions.Contains(pick))
        {
            targetName = pick;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{pick}' not found.\n");
            Console.ResetColor();
            return true;
        }

        _agentId = targetName;
        _agentOpts.AgentName = targetName;
        await _store.RecordLaunchAsync(targetName, cwd);
        await LoadAgentConfigAsync(targetName, cwd);
        await ResetSessionStateAsync();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Switched to agent '{targetName}'.\n");
        Console.ResetColor();
        return true;
    }

    private async Task ShowModelSelectionAsync()
    {
        var models = _deepseek.Models.Select(m => m.Name).Distinct().ToArray();
        if (models.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No models configured.");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        var currentModel = await _blockMemory.GetAgentModelAsync(_agentId!) ?? _deepseek.Model;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("── Models ─────────────────────────────");
        Console.ResetColor();
        for (var i = 0; i < models.Length; i++)
        {
            var marker = models[i] == currentModel ? " ←" : "";
            Console.ForegroundColor = models[i] == currentModel ? ConsoleColor.White : ConsoleColor.Gray;
            Console.WriteLine($"  {i + 1}. {models[i]}{marker}");
        }
        Console.ResetColor();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write("Select model (1-{0}) or Enter to cancel: ", models.Length);
            Console.ResetColor();
            var choice = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(choice))
            {
                Console.WriteLine();
                break;
            }
            if (int.TryParse(choice, out var idx) && idx >= 1 && idx <= models.Length)
            {
                var selected = models[idx - 1];
                await _blockMemory.SetAgentModelAsync(_agentId!, selected);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"Model changed to {selected}.");
                Console.ResetColor();
                Console.WriteLine();
                break;
            }
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid input. Enter 1-{models.Length} or press Enter to cancel.");
            Console.ResetColor();
        }
    }


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