using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IOptions<LlmOptions> _llmOpts;
    private readonly McpService _mcpService;
    private readonly ILogger _logger;
    private readonly string _defaultDir;
    private readonly string _tmpDir;

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
        IOptions<LlmOptions> llmOpts,
        McpService mcpService,
        ILogger<ToolRegistry> logger)
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
        _llmOpts = llmOpts;
        _mcpService = mcpService;
        _logger = logger;
        _defaultDir = Directory.GetCurrentDirectory();
        _tmpDir = Path.Combine(AppContext.BaseDirectory, "tmp");
    }

    public async Task<IReadOnlyList<AITool>> GetBuiltinToolsAsync(string agentId, bool includeMemory = false)
    {
        // Don't give subagent tools to subagents themselves — prevents recursive creation chaos
        var isSubAgent = _subAgentManager.Exists(agentId);

        var tools = new List<AITool>
        {
            BashTool.AsAIFunction(_bashManager, agentId, _cfgService, _tmpDir),
            BashBackTool.AsAIFunction(_bashManager, _cfgService, _tmpDir, agentId),
            FileReadTool.AsAIFunction(_cfgService, _defaultDir, agentId),
            FileWriteTool.AsAIFunction(_defaultDir),
            FilePatchTool.AsAIFunction(_defaultDir),
            TodoTool.AsTodoFunction(_agentStore, _blockStore, agentId, _cfgService),
            WebFetchTool.AsFetchFunction(_cfgService, agentId, _tmpDir),
            SearchTools.AsGlobFunction(_cfgService, _defaultDir, agentId, _logger),
            SearchTools.AsGrepFunction(_cfgService, _defaultDir, agentId, _logger),
            KVStoreTool.AsKvStoreFunction(_kvStore, _cfgService, _subAgentManager, agentId),
        };

        // Memory tool: available for main agent, or for subagents with saveMemory=true
        if (!isSubAgent || includeMemory)
            tools.Add(MemoryTool.AsAIFunction(_blockMemory, agentId, _cfgService));

        // MCP tools: available for all agents
        var mcpTools = await _mcpService.GetToolsAsync(agentId);
        tools.AddRange(mcpTools);

        // Subagent tools: only for main agent (prevents recursion)
        if (!isSubAgent)
        {
            tools.Add(SubAgentTool.AsSubAgentRunFunction(_subAgentManager, _agentManager, _scopeFactory, _agentStore, _blockStore, _cfgService, _llmOpts, agentId));
            tools.Add(SubAgentTool.AsSubAgentUseFunction(_subAgentManager, _agentManager, _scopeFactory, _agentStore, _blockStore, _llmOpts, agentId));
            tools.Add(SubAgentTool.AsSubAgentListFunction(_subAgentManager, _agentStore, _blockStore, agentId));
        }

        return tools;
    }
}
