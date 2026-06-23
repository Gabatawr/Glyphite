using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Cli.Services;

/// <summary>Manages agent identity, scope lifecycle, and session state.</summary>
public partial class SessionManager
{
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private readonly IAgentManager _agentManager;
    private readonly IAgentScopeFactory _scopeFactory;
    private readonly IConfigService _cfgService;
    private readonly ConsoleRenderer _renderer;
    private readonly InputHistory _inputHistory;
    private readonly ISessionConfigLoader _sessionConfigLoader;

    private AgentScope? _currentScope;
    private readonly string _cwd;
    private LlmOptions? _cachedLlm;

    public SessionManager(
        IAgentStore agentStore,
        IBlockStore blockStore,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IConfigService cfgService,
        ConsoleRenderer renderer,
        InputHistory inputHistory,
        ISessionConfigLoader sessionConfigLoader)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _agentManager = agentManager;
        _scopeFactory = scopeFactory;
        _cfgService = cfgService;
        _renderer = renderer;
        _inputHistory = inputHistory;
        _sessionConfigLoader = sessionConfigLoader;
        _cwd = Directory.GetCurrentDirectory();
    }

    public string AgentId { get; private set; } = string.Empty;
    public string AgentName => AgentId;
    public AgentScope? CurrentScope => _currentScope;

    /// <summary>Load fresh LlmOptions from config (hot-reloaded).</summary>
    public async Task<LlmOptions> GetLlmOptsAsync()
    {
        if (_cachedLlm is null)
            _cachedLlm = await _cfgService.GetOptionsAsync<LlmOptions>(LlmOptions.Section);
        return _cachedLlm;
    }

    /// <summary>Get the default model from config.</summary>
    public async Task<string> GetDefaultModelAsync()
    {
        var opts = await GetLlmOptsAsync();
        return opts.Model;
    }

    public ITurnProcessor TurnProcessor =>
        _currentScope?.TurnProcessor ?? throw new InvalidOperationException("Scope not initialized. SwitchScope() before use.");

    public IBlockMemoryProvider BlockMemory =>
        _currentScope?.BlockMemoryProvider ?? throw new InvalidOperationException("Scope not initialized. SwitchScope() before use.");

    /// <summary>Switch to a new agent scope. Call when creating/switching/cloning agents.</summary>
    public void SwitchScope()
    {
        _currentScope?.Dispose();
        _currentScope = _scopeFactory.CreateScope();
        _cachedLlm = null; // invalidate — will reload on next access
    }

    public async Task<(long Hit, long Miss, long Output, long LastHit, long LastMiss)> ResetSessionStateAsync()
    {
        var last = await _agentStore.GetLastUsageAsync(AgentId);
        return (last.Hit, last.Miss, last.Output, last.LastHit, last.LastMiss);
    }

    /// <summary>
    /// Create or resume an agent session. Sets AgentId and switches scope.
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

            var defaultModel = await GetDefaultModelAsync();
            AgentId = await _agentManager.CreateAgentAsync(firstName, defaultModel, cwd);
            SwitchScope();
            await _sessionConfigLoader.LoadConfigAsync(AgentId, cwd, _cwd);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Agent '{firstName}' created.\n");
            Console.ResetColor();
            return CreateOrResumeResult.Ready;
        }

        // Try resume last active agent in this directory
        var lastActive = await _agentStore.GetLastActiveAgentAsync(cwd);

        if (lastActive is not null)
        {
            // Resume directly
            AgentId = lastActive;
            SwitchScope();
            await _sessionConfigLoader.LoadConfigAsync(AgentId, cwd, _cwd);
            if (await BlockMemory.GetAgentModelAsync(AgentId) is null)
            {
                var defaultModel = await GetDefaultModelAsync();
                await BlockMemory.SetAgentModelAsync(AgentId, defaultModel);
            }
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

        var defaultModel = await GetDefaultModelAsync();
        var chatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            ModelId = await BlockMemory.GetAgentModelAsync(AgentId) ?? defaultModel,
        };

        var agentHome = await _agentStore.GetAgentHomePathAsync(AgentId);
        var homeIcon = string.Equals(agentHome, cwd, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(agentHome) ? " 🏠" : "";

        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
        var versionStr = version is not null ? $" v{version.Major}.{version.Minor}.{version.Build}" : "";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Glyphite CLI{versionStr} — {AgentName}{homeIcon}");
        Console.ResetColor();

        return chatOptions;
    }

}

public enum CreateOrResumeResult
{
    Ready,
    NeedsUserChoice
}
