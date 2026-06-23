using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Services;
using Glyphite.Host.Utils;

namespace Glyphite.Cli.Services;

/// <summary>Agent management commands (/new, /clone, /use, /delete, /models).</summary>
public partial class SessionManager
{
    public async Task<bool> HandleNewCommandAsync(string? name = null)
    {
        if (name is null)
        {
            // Flush any stale console input (left from previous ReadKey-based input reader)
            await Task.Delay(50);
            while (Console.KeyAvailable)
                Console.ReadKey(intercept: true);

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write("Enter new agent name: ");
            Console.ResetColor();
            name = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Cancelled.\n");
                Console.ResetColor();
                return true;
            }
        }

        if (!AgentManager.IsValidAgentName(name))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid agent name '{name}'. Use letters, digits, hyphens, underscores (max 100 chars).\n");
            Console.ResetColor();
            return true;
        }

        var cwd = _cwd;
        if (await _agentStore.AgentExistsAsync(name))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"Agent '{name}' already exists. Delete and recreate? (y/N): ");
            Console.ResetColor();
            var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (confirm is "y" or "yes")
            {
                await _agentStore.DeleteSessionAsync(name);
                AgentId = await _agentManager.CreateAgentAsync(name, await GetDefaultModelAsync(), cwd);
                SwitchScope();
                await _sessionConfigLoader.LoadConfigAsync(AgentId, cwd, _cwd);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Agent '{name}' recreated.\n");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Cancelled.\n");
                Console.ResetColor();
            }
            return true;
        }

        AgentId = await _agentManager.CreateAgentAsync(name, await GetDefaultModelAsync(), cwd);
        SwitchScope();
        await _sessionConfigLoader.LoadConfigAsync(AgentId, cwd, _cwd);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent '{name}' created.\n");
        Console.ResetColor();
        return true;
    }

    public async Task<bool> HandleCloneCommandAsync(string? name = null)
    {
        var sessions = await _agentStore.ListAgentsAsync();
        if (sessions.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No agents found. Create one with /new first.\n");
            Console.ResetColor();
            return true;
        }

        // If name provided and agent exists, prompt for clone name directly
        if (name is not null && sessions.Contains(name))
            return await PromptAndCloneAsync(name);

        // Name provided but doesn't exist — show source list, use name as clone name
        if (name is not null)
            Console.WriteLine($"Clone name: {name}\n");

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("Clone copies an agent's history to a new name.\n");

        Console.WriteLine("Pick source agent to copy from:");
        for (int i = 0; i < sessions.Count; i++)
        {
            var isCurrent = string.Equals(sessions[i], AgentId, StringComparison.Ordinal);
            var blockCount = await _blockStore.GetBlockCountAsync(sessions[i]);
            var usage = await _agentStore.GetLastUsageAsync(sessions[i]);
            var ctx = ToolCallHelper.FormatK(usage.LastHit + usage.LastMiss);
            var marker = isCurrent ? " ← current" : "";
            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {sessions[i]} ({blockCount} blocks, {ctx} ctx){marker}");
        }

        Console.ResetColor();
        Console.Write($"\nSelect source [1-{sessions.Count}, Enter=cancel]: ");
        var selection = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(selection))
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Cancelled.\n");
            Console.ResetColor();
            return true;
        }

        var sourceName = AgentPicker.Resolve(sessions, selection);
        if (sourceName is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{selection}' not found.\n");
            Console.ResetColor();
            return true;
        }

        return await PromptAndCloneAsync(sourceName, name);
    }

    public async Task<bool> HandleUseCommandAsync(string? name = null)
    {
        if (name is not null && await _agentStore.AgentExistsAsync(name))
        {
            AgentId = name;
            await _agentStore.RecordLaunchAsync(name, _cwd);
            SwitchScope();
            await _sessionConfigLoader.LoadConfigAsync(name, _cwd, _cwd);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Switched to agent '{name}'.\n");
            Console.ResetColor();
            return true;
        }

        var sessions = await _agentStore.ListAgentsAsync();
        if (sessions.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No agents found. Create one with /new first.\n");
            Console.ResetColor();
            return true;
        }

        if (name is not null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{name}' not found.\n");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("Select agent to use:\n");
        Console.ResetColor();

        var cwd = _cwd;
        for (int i = 0; i < sessions.Count; i++)
        {
            var isCurrent = string.Equals(sessions[i], AgentId, StringComparison.Ordinal);
            var agentHome = await _agentStore.GetAgentHomePathAsync(sessions[i]) ?? "";
            var createdAt = await _agentStore.GetAgentCreatedAtAsync(sessions[i]) ?? "";
            var blockCount = await _blockStore.GetBlockCountAsync(sessions[i]);
            var usage = await _agentStore.GetLastUsageAsync(sessions[i]);
            var ctx = ToolCallHelper.FormatK(usage.LastHit + usage.LastMiss);
            var lastLaunch = await _agentStore.GetLastLaunchPathAsync(sessions[i]);

            var marker = isCurrent ? " ← current" : "";
            var atHome = (string.Equals(agentHome, cwd, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(agentHome)) ? " [🏠 home]" : "";
            var createdDate = createdAt.Length >= 10 ? createdAt[..10] : "";
            var lastStr = lastLaunch is not null ? $" last: {lastLaunch}" : "";

            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {sessions[i]} ({blockCount} blocks, {ctx} ctx, {createdDate}){atHome}{lastStr}{marker}");
        }

        Console.ResetColor();
        Console.Write($"\nSelect agent [1-{sessions.Count}, Enter=cancel]: ");
        var pick = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(pick))
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Cancelled.\n");
            Console.ResetColor();
            return true;
        }

        var targetName = AgentPicker.Resolve(sessions, pick);
        if (targetName is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{pick}' not found.\n");
            Console.ResetColor();
            return true;
        }

        AgentId = targetName;
        await _agentStore.RecordLaunchAsync(targetName, cwd);
        SwitchScope();
        await _sessionConfigLoader.LoadConfigAsync(targetName, cwd, _cwd);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Switched to agent '{targetName}'.\n");
        Console.ResetColor();
        return true;
    }

    public async Task<bool> HandleDeleteCommandAsync(string? name = null)
    {
        var sessions = await _agentStore.ListAgentsAsync();
        var others = sessions.Where(s => !string.Equals(s, AgentId, StringComparison.Ordinal)).ToList();

        if (others.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No other agents to delete.\n");
            Console.ResetColor();
            return true;
        }

        if (name is not null && others.Contains(name))
            return await ConfirmAndDeleteAsync(name);

        if (name is not null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{name}' not found.\n");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("Select agent to delete:\n");
        Console.ResetColor();

        for (int i = 0; i < others.Count; i++)
        {
            var blockCount = await _blockStore.GetBlockCountAsync(others[i]);
            var usage = await _agentStore.GetLastUsageAsync(others[i]);
            var ctx = ToolCallHelper.FormatK(usage.LastHit + usage.LastMiss);
            var createdAt = await _agentStore.GetAgentCreatedAtAsync(others[i]) ?? "";
            var createdDate = createdAt.Length >= 10 ? createdAt[..10] : "";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {others[i]} ({blockCount} blocks, {ctx} ctx, {createdDate})");
        }

        Console.ResetColor();
        Console.Write($"\nSelect agent [1-{others.Count}, Enter=cancel]: ");
        var pick = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(pick))
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Cancelled.\n");
            Console.ResetColor();
            return true;
        }

        var targetName = AgentPicker.Resolve(others, pick);
        if (targetName is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{pick}' not found.\n");
            Console.ResetColor();
            return true;
        }

        return await ConfirmAndDeleteAsync(targetName);
    }

    public async Task ShowModelSelectionAsync()
    {
        var llm = await GetLlmOptsAsync();
        var models = llm.Models.Select(m => m.Name).Distinct().ToArray();
        if (models.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No models configured.");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        var defaultModel = await GetDefaultModelAsync();
        var currentModel = await BlockMemory.GetAgentModelAsync(AgentId!) ?? defaultModel;

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
                await BlockMemory.SetAgentModelAsync(AgentId!, selected);
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

    // ── Private helpers ──

    private async Task<bool> PromptAndCloneAsync(string sourceName, string? defaultCloneName = null)
    {
        if (defaultCloneName is null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write($"Enter new agent name (clone of '{sourceName}'): ");
            Console.ResetColor();
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Cancelled.\n");
                Console.ResetColor();
                return true;
            }

            defaultCloneName = input;
        }

        if (!AgentManager.IsValidAgentName(defaultCloneName))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid agent name '{defaultCloneName}'. Use letters, digits, hyphens, underscores (max 100 chars).\n");
            Console.ResetColor();
            return true;
        }

        if (await _agentStore.AgentExistsAsync(defaultCloneName))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Agent '{defaultCloneName}' already exists. Choose a different name.\n");
            Console.ResetColor();
            return true;
        }

        var cwd = _cwd;
        await _agentStore.ForkSessionAsync(sourceName, defaultCloneName, cwd);
        AgentId = defaultCloneName;
        SwitchScope();
        await _sessionConfigLoader.LoadConfigAsync(AgentId, cwd, _cwd);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent '{defaultCloneName}' cloned from '{sourceName}'.\n");
        Console.ResetColor();
        return true;
    }

    private async Task<bool> ConfirmAndDeleteAsync(string targetName)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write($"Delete agent '{targetName}'? This cannot be undone. (y/N): ");
        Console.ResetColor();
        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (confirm is not "y" and not "yes")
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Cancelled.\n");
            Console.ResetColor();
            return true;
        }

        await _agentStore.DeleteSessionAsync(targetName);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent '{targetName}' deleted.\n");
        Console.ResetColor();
        return true;
    }

    private static async Task<string> PromptAgentActionAsync()
    {
        string? action = null;
        while (action is null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("Choose an action:");
            Console.WriteLine("  0 — /new   (create a new agent)");
            Console.WriteLine("  1 — /use   (use an existing agent)");
            Console.WriteLine("  2 — /clone (clone an existing agent)");
            Console.ResetColor();
            Console.Write("Enter choice [0-2]: ");
            action = Console.ReadLine()?.Trim();
            Console.WriteLine();

            if (action is "0" or "/new") return "/new";
            if (action is "1" or "/use") return "/use";
            if (action is "2" or "/clone") return "/clone";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Invalid choice. Enter 0, 1, or 2.");
            Console.ResetColor();
            action = null;
        }
        return "/new";
    }

    private async Task HandleNewInDirectoryAsync(List<string> agents, string cwd)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write("Enter a name for your new agent: ");
        Console.ResetColor();
        var newName = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(newName) || !AgentManager.IsValidAgentName(newName))
        {
            newName = "MainAgent";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"Using default name '{newName}'.");
            Console.ResetColor();
        }
        if (await _agentStore.AgentExistsAsync(newName))
            newName = await GenerateUniqueNameAsync(newName);
        AgentId = await _agentManager.CreateAgentAsync(newName, await GetDefaultModelAsync(), cwd);
        SwitchScope();
        await _sessionConfigLoader.LoadConfigAsync(AgentId, cwd, _cwd);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent '{newName}' created.\n");
        Console.ResetColor();
    }

    private async Task HandleUseInDirectoryAsync(List<string> agents)
    {
        Console.WriteLine("Select agent to use:\n");
        for (int i = 0; i < agents.Count; i++)
        {
            var homePath = await _agentStore.GetAgentHomePathAsync(agents[i]) ?? "";
            var blockCount = await _blockStore.GetBlockCountAsync(agents[i]);
            var usage = await _agentStore.GetLastUsageAsync(agents[i]);
            var ctx = ToolCallHelper.FormatK(usage.LastHit + usage.LastMiss);
            var createdAt = (await _agentStore.GetAgentCreatedAtAsync(agents[i]) ?? "")[..10];
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {agents[i]} ({blockCount} blocks, {ctx} ctx, {createdAt})");
        }
        Console.ResetColor();
        Console.Write($"\nSelect agent [1-{agents.Count}]: ");
        var pick = Console.ReadLine()?.Trim();
        var resolved = AgentPicker.Resolve(agents, pick);
        AgentId = resolved ?? agents[0];
        if (resolved is null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"Agent '{pick}' not found, using '{AgentId}'.\n");
            Console.ResetColor();
        }
        SwitchScope();
        await _sessionConfigLoader.LoadConfigAsync(AgentId, _cwd, _cwd);
        if (await BlockMemory.GetAgentModelAsync(AgentId) is null)
            await BlockMemory.SetAgentModelAsync(AgentId, await GetDefaultModelAsync());
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Using agent '{AgentId}'.\n");
        Console.ResetColor();
    }

    private async Task HandleCloneInDirectoryAsync(List<string> agents, string cwd)
    {
        Console.WriteLine("Pick source agent to copy from:\n");
        for (int i = 0; i < agents.Count; i++)
        {
            var blockCount = await _blockStore.GetBlockCountAsync(agents[i]);
            var usage = await _agentStore.GetLastUsageAsync(agents[i]);
            var ctx = ToolCallHelper.FormatK(usage.LastHit + usage.LastMiss);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {agents[i]} ({blockCount} blocks, {ctx} ctx)");
        }
        Console.ResetColor();
        Console.Write($"Select source [1-{agents.Count}]: ");
        var srcPick = Console.ReadLine()?.Trim();
        var resolvedSource = AgentPicker.Resolve(agents, srcPick);
        var sourceName = resolvedSource ?? agents[0];
        if (resolvedSource is null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"Agent '{srcPick}' not found, using '{sourceName}'.\n");
            Console.ResetColor();
        }
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write($"Enter new agent name (clone of '{sourceName}'): ");
        Console.ResetColor();
        var cloneName = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(cloneName) || !AgentManager.IsValidAgentName(cloneName))
        {
            cloneName = sourceName + "-clone";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"Using default name '{cloneName}'.");
            Console.ResetColor();
        }
        if (await _agentStore.AgentExistsAsync(cloneName))
            cloneName = await GenerateUniqueNameAsync(cloneName);
        await _agentStore.ForkSessionAsync(sourceName, cloneName, cwd);
        AgentId = cloneName;
        SwitchScope();
        await _sessionConfigLoader.LoadConfigAsync(AgentId, cwd, _cwd);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Agent '{cloneName}' cloned from '{sourceName}'.\n");
        Console.ResetColor();
    }

    private static async Task<string> GenerateUniqueNameAsync(string baseName)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Agent '{baseName}' already exists. Using unique name.");
        Console.ResetColor();
        return baseName + "-" + Guid.NewGuid().ToString("N")[..6];
    }
}
