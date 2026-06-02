using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Cli.Services;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private readonly IConfiguration _config;
    private readonly IMemoryStore _store;
    private readonly IBlockMemoryProvider _blockMemory;
    private readonly ITurnProcessor _turnProcessor;
    private readonly IConfigService _cfgService;
    private readonly IAgentManager _agentManager;
    private readonly ConsoleRenderer _renderer;
    private readonly DeepSeekOptions _deepseek;
    private readonly AgentOptions _agentOpts;
    private readonly ToolStreamingOptions _streamOpts;
    private readonly CompressionOptions _compressionOpts;
    private readonly ContextSnapshotService _snapshot;

    private string _agentId = string.Empty;
    private long _lastTurnHit;
    private long _lastTurnMiss;
    private long _lastTurnOutput;

    public ChatRepl(
        IConfiguration config,
        IMemoryStore store,
        IBlockMemoryProvider blockMemory,
        ITurnProcessor turnProcessor,
        IConfigService cfgService,
        IAgentManager agentManager,
        ConsoleRenderer renderer,
        IOptions<DeepSeekOptions> deepseek,
        IOptions<AgentOptions> agentOpts,
        IOptions<ToolStreamingOptions> streamOpts,
        IOptions<CompressionOptions> compressionOpts,
        ContextSnapshotService snapshot)
    {
        _config = config;
        _store = store;
        _blockMemory = blockMemory;
        _turnProcessor = turnProcessor;
        _cfgService = cfgService;
        _agentManager = agentManager;
        _renderer = renderer;
        _deepseek = deepseek.Value;
        _agentOpts = agentOpts.Value;
        _streamOpts = streamOpts.Value;
        _compressionOpts = compressionOpts.Value;
        _snapshot = snapshot;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var cwd = Directory.GetCurrentDirectory();

        _cfgService.LogAction = msg => Log.Information("{ConfigMessage}", msg);
        await _cfgService.InitializeAsync();

        // ── Agent resolution: resume or choose ──
        var agents = await _store.ListAgentsAsync();

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

            _agentId = await _agentManager.CreateAgentAsync(firstName, _deepseek.Model, cwd);
            _agentOpts.AgentName = firstName;
            await ResetSessionStateAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Agent '{firstName}' created.\n");
            Console.ResetColor();
        }
        else
        {
            // Try resume last active agent in this directory
            var lastActive = await _store.GetLastActiveAgentAsync(cwd);

            if (lastActive is not null)
            {
                // Resume directly
                _agentId = lastActive;
                _agentOpts.AgentName = lastActive;
                await ResetSessionStateAsync();
                if (await _blockMemory.GetAgentModelAsync(_agentId) is null)
                    await _blockMemory.SetAgentModelAsync(_agentId, _deepseek.Model);
            }
            else
            {
                // No agent was active in this directory — let user choose
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("Welcome to Glyphite!");
                Console.WriteLine("No agent has been used in this directory yet.");
                Console.WriteLine();
                Console.ResetColor();

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

                    if (action is "0" or "/new") { action = "/new"; break; }
                    if (action is "1" or "/use") { action = "/use"; break; }
                    if (action is "2" or "/clone") { action = "/clone"; break; }
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Invalid choice. Enter 0, 1, or 2.");
                    Console.ResetColor();
                    action = null;
                }

                if (action == "/new")
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
                    if (await _store.AgentExistsAsync(newName))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Agent '{newName}' already exists. Use /use or /fork.");
                        Console.ResetColor();
                        newName = "MainAgent-" + Guid.NewGuid().ToString("N")[..6];
                        Console.ForegroundColor = ConsoleColor.Gray;
                        Console.WriteLine($"Using unique name '{newName}'.");
                        Console.ResetColor();
                    }
                    _agentId = await _agentManager.CreateAgentAsync(newName, _deepseek.Model, cwd);
                    _agentOpts.AgentName = newName;
                    await ResetSessionStateAsync();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Agent '{newName}' created.\n");
                    Console.ResetColor();
                }
                else if (action == "/use")
                {
                    // Show agent list, let user pick
                    Console.WriteLine("Select agent to use:\n");
                    for (int i = 0; i < agents.Count; i++)
                    {
                        var homePath = await _store.GetAgentHomePathAsync(agents[i]) ?? "";
                        var blockCount = await _store.GetBlockCountAsync(agents[i]);
                        var createdAt = (await _store.GetAgentCreatedAtAsync(agents[i]) ?? "")[..10];
                        Console.ForegroundColor = ConsoleColor.Gray;
                        Console.WriteLine($"  [{i + 1}] {agents[i]} ({blockCount} blocks, {createdAt})");
                    }
                    Console.ResetColor();
                    Console.Write($"\nSelect agent [1-{agents.Count}]: ");
                    var pick = Console.ReadLine()?.Trim();
                    if (int.TryParse(pick, out var idx) && idx >= 1 && idx <= agents.Count)
                        _agentId = agents[idx - 1];
                    else if (agents.Contains(pick ?? ""))
                        _agentId = pick!;
                    else
                        _agentId = agents[0];
                    _agentOpts.AgentName = _agentId;
                    await ResetSessionStateAsync();
                    if (await _blockMemory.GetAgentModelAsync(_agentId) is null)
                        await _blockMemory.SetAgentModelAsync(_agentId, _deepseek.Model);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Using agent '{_agentId}'.\n");
                    Console.ResetColor();
                }
                else if (action == "/clone")
                {
                    // List agents, pick source, then ask for new name
                    Console.WriteLine("Pick source agent to copy from:\n");
                    for (int i = 0; i < agents.Count; i++)
                    {
                        var blockCount = await _store.GetBlockCountAsync(agents[i]);
                        Console.ForegroundColor = ConsoleColor.Gray;
                        Console.WriteLine($"  [{i + 1}] {agents[i]} ({blockCount} blocks)");
                    }
                    Console.ResetColor();
                    Console.Write($"Select source [1-{agents.Count}]: ");
                    var srcPick = Console.ReadLine()?.Trim();
                    string sourceName;
                    if (int.TryParse(srcPick, out var srcIdx) && srcIdx >= 1 && srcIdx <= agents.Count)
                        sourceName = agents[srcIdx - 1];
                    else if (agents.Contains(srcPick ?? ""))
                        sourceName = srcPick!;
                    else
                        sourceName = agents[0];
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
                    if (await _store.AgentExistsAsync(cloneName))
                    {
                        cloneName = sourceName + "-" + Guid.NewGuid().ToString("N")[..6];
                        Console.ForegroundColor = ConsoleColor.Gray;
                        Console.WriteLine($"Using unique name '{cloneName}'.");
                        Console.ResetColor();
                    }
                    await _store.ForkSessionAsync(sourceName, cloneName, cwd);
                    _agentId = cloneName;
                    _agentOpts.AgentName = cloneName;
                    await ResetSessionStateAsync();
                    await LoadAgentConfigAsync(_agentId, cwd);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Agent '{cloneName}' cloned from '{sourceName}'.\n");
                    Console.ResetColor();
                }
            }
        }

        // Record this launch for the current agent
        await _store.RecordLaunchAsync(_agentId, cwd);

        var agentsPath = Path.Combine(cwd, "AGENTS.md");
        _blockMemory.AgentFilePath = File.Exists(agentsPath) ? agentsPath : null;

        await _renderer.ReplayBlocksAsync(_agentId, _store);

        // Load existing user messages into input history
        var userBlocks = await _store.LoadBlocksByTypeAsync(_agentId, BlockType.user_message, null, desc: false);
        foreach (var b in userBlocks)
            _inputHistory.Add(b.Content);

        // Seed built-in commands so Up at "/" always shows something
        foreach (var cmd in new[] { "/exit", "/new", "/clone", "/use", "/stats", "/models", "/reload", "/diff", "/diffprint" })
        {
            if (!_inputHistory.Contains(cmd))
                _inputHistory.Add(cmd);
        }

        // Load system prompt from embedded resource
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var systemPrompt = "You are Glyphite, an expert software engineer and coding agent. You are precise, thorough, and efficient.";
        using (var stream = assembly.GetManifestResourceStream("glyphite.system-prompt.md"))
        {
            if (stream is not null)
                using (var reader = new StreamReader(stream))
                    systemPrompt = reader.ReadToEnd().Trim();
        }

        var chatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            ModelId = await _blockMemory.GetAgentModelAsync(_agentId) ?? _deepseek.Model,
        };

        var agentHome = await _store.GetAgentHomePathAsync(_agentId);
        var homeIcon = string.Equals(agentHome, cwd, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(agentHome) ? " 🏠" : "";

        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
        var versionStr = version is not null ? $" v{version.Major}.{version.Minor}.{version.Build}" : "";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Glyphite CLI{versionStr} — {_agentOpts.AgentName}{homeIcon}");
        Console.ResetColor();

        await UpdatePromptPrefixAsync();

        while (!ct.IsCancellationRequested)
        {
            var input = await ReadLineWithHistoryAsync();
            if (input is null || input == "/exit")
                break;

            if (await HandleCommandAsync(input))
                continue;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            Console.WriteLine();

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var cancelMonitor = MonitorEscapeAsync(turnCts);
            await ProcessInputAsync(input, chatOptions, turnCts.Token);
            turnCts.Cancel(); // stop monitor
            try { await cancelMonitor; } catch { }
            Console.WriteLine();
            Console.WriteLine();
            await UpdatePromptPrefixAsync();
        }

        Log.CloseAndFlush();
    }
}
