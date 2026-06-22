using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Glyphite.Cli.Services;

/// <summary>Manages agent identity, scope lifecycle, and session state.</summary>
public class SessionManager
{
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private readonly IAgentManager _agentManager;
    private readonly IAgentScopeFactory _scopeFactory;
    private readonly IConfigService _cfgService;
    private readonly ConsoleRenderer _renderer;
    private readonly DeepSeekOptions _deepseek;
    private readonly AgentOptions _agentOpts;
    private readonly InputHistory _inputHistory;
    private readonly ConfigLoader _configLoader;

    private AgentScope? _currentScope;
    private readonly string _cwd;

    public SessionManager(
        IAgentStore agentStore,
        IBlockStore blockStore,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IConfigService cfgService,
        ConsoleRenderer renderer,
        IOptions<DeepSeekOptions> deepseek,
        IOptions<AgentOptions> agentOpts,
        InputHistory inputHistory)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _agentManager = agentManager;
        _scopeFactory = scopeFactory;
        _cfgService = cfgService;
        _renderer = renderer;
        _deepseek = deepseek.Value;
        _agentOpts = agentOpts.Value;
        _inputHistory = inputHistory;
        _configLoader = new ConfigLoader(cfgService, agentStore);
        _cwd = Directory.GetCurrentDirectory();
    }

    public string AgentId { get; private set; } = string.Empty;
    public string AgentName => _agentOpts.AgentName;
    public AgentScope? CurrentScope => _currentScope;
    public DeepSeekOptions DeepSeekOpts => _deepseek;

    public ITurnProcessor TurnProcessor =>
        _currentScope?.TurnProcessor ?? throw new InvalidOperationException("Scope not initialized. SwitchScope() before use.");

    public IBlockMemoryProvider BlockMemory =>
        _currentScope?.BlockMemoryProvider ?? throw new InvalidOperationException("Scope not initialized. SwitchScope() before use.");

    /// <summary>Switch to a new agent scope. Call when creating/switching/cloning agents.</summary>
    public void SwitchScope()
    {
        _currentScope?.Dispose();
        _currentScope = _scopeFactory.CreateScope();
    }

    public async Task<(long Hit, long Miss, long Output, long LastHit, long LastMiss)> ResetSessionStateAsync()
    {
        var last = await _agentStore.GetLastUsageAsync(AgentId);
        return (last.Hit, last.Miss, last.Output, last.LastHit, last.LastMiss);
    }

    /// <summary>
    /// Create or resume an agent session. Sets AgentId and switches scope.
    /// Returns true if a session was created/resumed, false if user needs to pick.
    /// </summary>
    public async Task<CreateOrResumeResult> CreateOrResumeAgentAsync(string cwd)
    {
        _cfgService.LogAction = msg => Serilog.Log.Information("{ConfigMessage}", msg);
        await _cfgService.InitializeAsync(replaceSections: ["McpServers"]);

        var agents = await _agentStore.ListAgentsAsync();

        if (agents.Count == 0)
        {
            // First run — must create an agent
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("Welcome to Glyphite! This is your first run.");
            Console.Write("Enter a name for your agent: ");
            Console.ResetColor();
            var firstName = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(firstName) || !AgentManager.IsValidAgentName(firstName))
            {
                firstName = "MainAgent";
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"Using default name '{firstName}'.");
                Console.ResetColor();
            }

            AgentId = await _agentManager.CreateAgentAsync(firstName, _deepseek.Model, cwd);
            _agentOpts.AgentName = firstName;
            SwitchScope();
            await _configLoader.LoadAgentConfigAsync(AgentId, cwd);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Agent '{firstName}' created.\n");
            Console.ResetColor();
            return CreateOrResumeResult.Ready;
        }

        // Eager-load all agent configs in cwd
        await _configLoader.LoadAllAgentConfigsAsync(cwd);

        // Try resume last active agent in this directory
        var lastActive = await _agentStore.GetLastActiveAgentAsync(cwd);

        if (lastActive is not null)
        {
            // Resume directly
            AgentId = lastActive;
            _agentOpts.AgentName = lastActive;
            SwitchScope();
            await _configLoader.LoadAgentConfigAsync(AgentId, cwd);
            if (await BlockMemory.GetAgentModelAsync(AgentId) is null)
                await BlockMemory.SetAgentModelAsync(AgentId, _deepseek.Model);
            return CreateOrResumeResult.Ready;
        }

        // No agent was active in this directory — let user choose
        return CreateOrResumeResult.NeedsUserChoice;
    }

    public async Task<CreateOrResumeResult> HandleUserChoiceAsync(string cwd)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("Welcome to Glyphite!");
        Console.WriteLine("No agent has been used in this directory yet.");
        Console.WriteLine();
        Console.ResetColor();

        var action = await PromptAgentActionAsync();
        var agents = await _agentStore.ListAgentsAsync();

        switch (action)
        {
            case "/new":
                await HandleNewInDirectoryAsync(agents, cwd);
                break;
            case "/use":
                await HandleUseInDirectoryAsync(agents);
                break;
            case "/clone":
                await HandleCloneInDirectoryAsync(agents, cwd);
                break;
        }

        return CreateOrResumeResult.Ready;
    }

    public async Task<ChatOptions> InitializeAfterAgentAsync(string cwd)
    {
        _renderer.AgentCwd = cwd;

        // Record this launch for the current agent
        await _agentStore.RecordLaunchAsync(AgentId, cwd);

        var agentsPath = Path.Combine(cwd, "AGENTS.md");
        BlockMemory.AgentFilePath = File.Exists(agentsPath) ? agentsPath : null;

        await _renderer.ReplayBlocksAsync(AgentId, _blockStore);

        // Load existing user messages into input history
        var userBlocks = await _blockStore.LoadBlocksByTypeAsync(AgentId, BlockType.user_message, null, desc: false);
        foreach (var b in userBlocks)
            _inputHistory.Add(b.Content);

        // Seed built-in commands so Up at "/" always shows something
        foreach (var cmd in new[] { "/exit", "/new", "/clone", "/use", "/delete", "/stats", "/models" })
        {
            if (!_inputHistory.Contains(cmd))
                _inputHistory.Add(cmd);
        }

        // Load system prompt from embedded resource
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var systemPrompt = "You are Glyphite, an expert software engineer and coding agent. You are precise, thorough, and efficient.";
        using (var stream = assembly.GetManifestResourceStream("Glyphite.Cli.system-prompt.md"))
        {
            if (stream is not null)
                using (var reader = new StreamReader(stream))
                    systemPrompt = reader.ReadToEnd().Trim();
        }

        var chatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            ModelId = await BlockMemory.GetAgentModelAsync(AgentId) ?? _deepseek.Model,
        };

        var agentHome = await _agentStore.GetAgentHomePathAsync(AgentId);
        var homeIcon = string.Equals(agentHome, cwd, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(agentHome) ? " 🏠" : "";

        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
        var versionStr = version is not null ? $" v{version.Major}.{version.Minor}.{version.Build}" : "";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Glyphite CLI{versionStr} — {_agentOpts.AgentName}{homeIcon}");
        Console.ResetColor();

        return chatOptions;
    }

    // ── Agent commands ──

    public async Task<bool> HandleNewCommandAsync(string? name = null)
    {
        if (name is null)
        {
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
                AgentId = await _agentManager.CreateAgentAsync(name, _deepseek.Model, cwd);
                _agentOpts.AgentName = name;
                SwitchScope();
                await _configLoader.LoadAgentConfigAsync(AgentId, cwd);
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

        AgentId = await _agentManager.CreateAgentAsync(name, _deepseek.Model, cwd);
        _agentOpts.AgentName = name;
        SwitchScope();
        await _configLoader.LoadAgentConfigAsync(AgentId, cwd);
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

        // Name provided but doesn't exist → show source list, use name as clone name
        if (name is not null)
            Console.WriteLine($"Clone name: {name}\n");

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("Clone copies an agent's history to a new name.\n");

        Console.WriteLine("Pick source agent to copy from:");
        for (int i = 0; i < sessions.Count; i++)
        {
            var isCurrent = string.Equals(sessions[i], AgentId, StringComparison.Ordinal);
            var blockCount = await _blockStore.GetBlockCountAsync(sessions[i]);
            var marker = isCurrent ? " ← current" : "";
            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {sessions[i]} ({blockCount} blocks){marker}");
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
            _agentOpts.AgentName = name;
            await _agentStore.RecordLaunchAsync(name, _cwd);
            SwitchScope();
            await _configLoader.LoadAgentConfigAsync(name, _cwd);
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
            var lastLaunch = await _agentStore.GetLastLaunchPathAsync(sessions[i]);

            var marker = isCurrent ? " ← current" : "";
            var atHome = (string.Equals(agentHome, cwd, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(agentHome)) ? " [🏠 home]" : "";
            var createdDate = createdAt.Length >= 10 ? createdAt[..10] : "";
            var lastStr = lastLaunch is not null ? $" last: {lastLaunch}" : "";

            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {sessions[i]} ({blockCount} blocks, {createdDate}){atHome}{lastStr}{marker}");
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
        _agentOpts.AgentName = targetName;
        await _agentStore.RecordLaunchAsync(targetName, cwd);
        SwitchScope();
        await _configLoader.LoadAgentConfigAsync(targetName, cwd);
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
            var createdAt = await _agentStore.GetAgentCreatedAtAsync(others[i]) ?? "";
            var createdDate = createdAt.Length >= 10 ? createdAt[..10] : "";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {others[i]} ({blockCount} blocks, {createdDate})");
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
        var models = _deepseek.Models.Select(m => m.Name).Distinct().ToArray();
        if (models.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No models configured.");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        var currentModel = await BlockMemory.GetAgentModelAsync(AgentId!) ?? _deepseek.Model;

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
        _agentOpts.AgentName = defaultCloneName;
        SwitchScope();
        await _configLoader.LoadAgentConfigAsync(AgentId, cwd);

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
        AgentId = await _agentManager.CreateAgentAsync(newName, _deepseek.Model, cwd);
        _agentOpts.AgentName = newName;
        SwitchScope();
        await _configLoader.LoadAgentConfigAsync(AgentId, cwd);
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
            var createdAt = (await _agentStore.GetAgentCreatedAtAsync(agents[i]) ?? "")[..10];
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {agents[i]} ({blockCount} blocks, {createdAt})");
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
        _agentOpts.AgentName = AgentId;
        SwitchScope();
        await _configLoader.LoadAgentConfigAsync(AgentId, _cwd);
        if (await BlockMemory.GetAgentModelAsync(AgentId) is null)
            await BlockMemory.SetAgentModelAsync(AgentId, _deepseek.Model);
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
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  [{i + 1}] {agents[i]} ({blockCount} blocks)");
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
        _agentOpts.AgentName = cloneName;
        SwitchScope();
        await _configLoader.LoadAgentConfigAsync(AgentId, cwd);
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

public enum CreateOrResumeResult
{
    Ready,
    NeedsUserChoice
}
