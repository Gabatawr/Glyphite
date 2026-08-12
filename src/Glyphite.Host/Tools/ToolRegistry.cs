using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;


namespace Glyphite.Host.Tools;

public class ToolRegistry : IToolRegistry
{
    private readonly IBashSessionManager _bashManager;
    private readonly IConfigService _cfgService;
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private readonly IBlockMemoryProvider _blockMemory;
    private readonly IKVStore _kvStore;
    private readonly SubAgentManager _subAgentManager;
    private readonly IAgentManager _agentManager;
    private readonly IAgentScopeFactory _scopeFactory;
    private readonly McpService _mcpService;
    private readonly ILogger _logger;
    private readonly ISafetyChecker _safetyChecker;
    private readonly string _defaultDir;
    private readonly string _tmpDir;
    private readonly IConfiguration _glyphiteConfig;

    public ToolRegistry(
        IBashSessionManager bashManager,
        IConfigService cfgService,
        IAgentStore agentStore,
        IBlockStore blockStore,
        IBlockMemoryProvider blockMemory,
        IKVStore kvStore,
        SubAgentManager subAgentManager,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        McpService mcpService,
        IConfiguration configuration,
        ILogger<ToolRegistry> logger,
        ISafetyChecker safetyChecker)
    {
        _bashManager = bashManager;
        _cfgService = cfgService;
        _agentStore = agentStore;
        _blockStore = blockStore;
        _blockMemory = blockMemory;
        _kvStore = kvStore;
        _subAgentManager = subAgentManager;
        _agentManager = agentManager;
        _scopeFactory = scopeFactory;
        _mcpService = mcpService;
        _logger = logger;
        _safetyChecker = safetyChecker;
        _defaultDir = Directory.GetCurrentDirectory();
        _tmpDir = Path.Combine(AppContext.BaseDirectory, "tmp");
        _glyphiteConfig = configuration.GetSection("Glyphite");
    }

    public async Task<IReadOnlyList<AITool>> GetBuiltinToolsAsync(string agentId, bool includeMemory = false)
    {
        // Don't give subagent tools to subagents themselves — prevents recursive creation chaos
        var isSubAgent = _subAgentManager.Exists(agentId);

        var toolExec = LoadToolExecution();

        var readFileMaxSize = toolExec.TryGetValue("read_file", out var readOpts) ? (int?)readOpts.MaxSize : null;

        var tools = new List<AITool>
        {
            WrapWithConfig(BashTool.AsAIFunction(_bashManager, agentId, _cfgService, _kvStore, isSubAgent, _safetyChecker), toolExec, "bash", _tmpDir, agentId),
            WrapWithConfig(BashBackTool.AsAIFunction(_bashManager, _cfgService, agentId), toolExec, "bash_back", _tmpDir, agentId),
            WrapWithConfig(FileReadTool.AsAIFunction(_cfgService, _defaultDir, agentId, readFileMaxSize), toolExec, "read_file", _tmpDir, agentId),
            WrapWithConfig(FileWriteTool.AsAIFunction(_defaultDir), toolExec, "write_file", _tmpDir, agentId),
            WrapWithConfig(FilePatchTool.AsAIFunction(_defaultDir), toolExec, "patch_file", _tmpDir, agentId),
            WrapWithConfig(TodoTool.AsTodoFunction(_agentStore, _blockStore, agentId, _cfgService), toolExec, "todo", _tmpDir, agentId),
            WrapWithConfig(WebFetchTool.AsFetchFunction(_cfgService, agentId), toolExec, "fetch_web", _tmpDir, agentId),
            WrapWithConfig(SearchTools.AsGlobFunction(_cfgService, _defaultDir, agentId, _logger), toolExec, "search_glob", _tmpDir, agentId),
            WrapWithConfig(SearchTools.AsGrepFunction(_cfgService, _defaultDir, agentId, _logger), toolExec, "search_grep", _tmpDir, agentId),
            WrapWithConfig(KVStoreTool.AsKvStoreFunction(_kvStore, _cfgService, _subAgentManager, agentId), toolExec, "kvstore", _tmpDir, agentId),
        };

        // Pocket tools: per-agent toolbox of user-defined aliases (available for all agents)
        foreach (var pocketFn in PocketTool.AsManagementFunctions(
                     _kvStore, _cfgService, _bashManager, _subAgentManager, _safetyChecker, agentId, isSubAgent))
        {
            tools.Add(WrapWithConfig(pocketFn, toolExec, pocketFn.Name, _tmpDir, agentId));
        }

        // Memory tool: available for main agent, or for subagents with saveMemory=true
        if (!isSubAgent || includeMemory)
            tools.Add(WrapWithConfig(MemoryTool.AsAIFunction(_blockMemory, agentId, _cfgService), toolExec, "memory", _tmpDir, agentId));

        // MCP tools: available for all agents. McpService wraps each tool with its own
        // ToolConfigDecorator (McpExecution settings + ToolExecutionDefaults), so they
        // are added here unwrapped — no need to re-wrap.
        var mcpTools = await _mcpService.GetToolsAsync(agentId);
        tools.AddRange(mcpTools);

        // Subagent tools: only for main agent (prevents recursion)
        if (!isSubAgent)
        {
            tools.Add(WrapWithConfig(SubAgentTool.AsSubAgentRunFunction(_subAgentManager, _agentManager, _scopeFactory, _agentStore, _blockStore, _cfgService, _bashManager, agentId), toolExec, "subagent_run", _tmpDir, agentId));
            tools.Add(WrapWithConfig(SubAgentTool.AsSubAgentUseFunction(_subAgentManager, _agentManager, _scopeFactory, _agentStore, _blockStore, _cfgService, agentId), toolExec, "subagent_use", _tmpDir, agentId));
            tools.Add(WrapWithConfig(SubAgentTool.AsSubAgentListFunction(_subAgentManager, _agentStore, _blockStore, agentId), toolExec, "subagent_list", _tmpDir, agentId));
        }

        // Materialized pocket entries → native typed tools.
        // Only entries explicitly flagged as favorites (materialize=true) become native tools.
        // Native names carry the "_pocket" suffix; skipped when that name collides with an already-registered tool.
        // Local favorites are materialized first — a local entry shadows a global one with the same native name;
        // a materialized global (shared) entry appears as a native tool for all agents.
        async Task AddMaterialized(PocketTool.PocketEntry entry, bool isGlobal)
        {
            if (!entry.Materialize) return;

            var nativeName = PocketTool.MaterializedName(entry.Name);
            if (tools.Any(t => string.Equals(t.Name, nativeName, StringComparison.OrdinalIgnoreCase)))
                return;

            var runner = PocketTool.CreateRunner(_bashManager, _cfgService, _kvStore, isSubAgent, _safetyChecker, agentId);
            tools.Add(WrapWithConfig(PocketTool.AsPocketFunction(entry, runner, isGlobal), toolExec, nativeName, _tmpDir, agentId));
        }

        foreach (var entry in await PocketTool.LoadEntriesAsync(_kvStore, agentId))
            await AddMaterialized(entry, isGlobal: false);

        foreach (var entry in await PocketTool.LoadGlobalEntriesAsync(_kvStore))
            await AddMaterialized(entry, isGlobal: true);

        return tools;
    }

    /// <summary>
    /// Loads the ToolExecution config section into a lookup dictionary.
    /// Returns empty dict if section is missing or unparseable.
    /// </summary>
    private Dictionary<string, ToolExecutionOptionsEntry> LoadToolExecution()
    {
        try
        {
            var section = _glyphiteConfig.GetSection("ToolExecution");
            var entries = section.Get<ToolExecutionEntry[]>();
            if (entries is null || entries.Length == 0)
                return [];

            return entries
                .Where(e => e is not null && !string.IsNullOrEmpty(e.Tool))
                .Select(e => KeyValuePair.Create(e.Tool, e.Options ?? new ToolExecutionOptionsEntry()))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse ToolExecution config section — using defaults");
            return [];
        }
    }

    /// <summary>
    /// Wraps an <see cref="AIFunction"/> with <see cref="ToolConfigDecorator"/>
    /// if execution settings exist for this tool name.
    /// </summary>
    private static AIFunction WrapWithConfig(
        AIFunction tool,
        Dictionary<string, ToolExecutionOptionsEntry> toolExec,
        string toolName,
        string tmpDir = "",
        string? agentId = null)
    {
        if (toolExec.TryGetValue(toolName, out var opts))
        {
            return new ToolConfigDecorator(
                tool,
                peekDefault: opts.Peek,
                contentMaxSizeDefault: opts.MaxSize,
                timeoutSecondsDefault: opts.Timeout,
                tmpDir: tmpDir,
                agentId: agentId);
        }

        // No config for this tool — still wrap with defaults
        return new ToolConfigDecorator(
            tool,
            peekDefault: ToolExecutionDefaults.Peek,
            contentMaxSizeDefault: ToolExecutionDefaults.ContentMaxSize,
            timeoutSecondsDefault: ToolExecutionDefaults.BuiltinTimeoutSeconds,
            tmpDir: tmpDir,
            agentId: agentId);
    }
}