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
    private readonly SubAgentManager _subAgentManager;
    private readonly IAgentManager _agentManager;
    private readonly IAgentScopeFactory _scopeFactory;
    private readonly IOptions<LlmOptions> _llmOpts;
    private readonly McpService _mcpService;
    private readonly ILogger _logger;
    private readonly string _defaultDir;

    public ToolRegistry(
        IBashSessionManager bashManager,
        IConfigService cfgService,
        IAgentStore agentStore,
        IBlockStore blockStore,
        IBlockMemoryProvider blockMemory,
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
        _subAgentManager = subAgentManager;
        _agentManager = agentManager;
        _scopeFactory = scopeFactory;
        _llmOpts = llmOpts;
        _mcpService = mcpService;
        _logger = logger;
        _defaultDir = Directory.GetCurrentDirectory();
    }

    public async Task<IReadOnlyList<AITool>> GetBuiltinToolsAsync(string sessionId, bool includeMemory = false)
    {
        // Don't give subagent tools to subagents themselves — prevents recursive creation chaos
        var isSubAgent = _subAgentManager.Exists(sessionId);

        var tools = new List<AITool>
        {
            BashTool.AsAIFunction(_bashManager, sessionId, _cfgService),
            FileReadTool.AsAIFunction(_cfgService, _defaultDir, sessionId),
            FileWriteTool.AsAIFunction(_defaultDir),
            FilePatchTool.AsAIFunction(_defaultDir),
            TodoTool.AsTodoFunction(_agentStore, _blockStore, sessionId, _cfgService),
            WebFetchTool.AsFetchFunction(_cfgService, sessionId),
            SearchTools.AsGlobFunction(_cfgService, _defaultDir, sessionId, _logger),
            SearchTools.AsGrepFunction(_cfgService, _defaultDir, sessionId, _logger),
        };

        // Memory tool: available for main agent, or for subagents with saveMemory=true
        if (!isSubAgent || includeMemory)
            tools.Add(MemoryTool.AsAIFunction(_blockMemory, sessionId, _cfgService));

        // MCP tools: available for all agents
        var mcpTools = await _mcpService.GetToolsAsync(sessionId);
        tools.AddRange(mcpTools);

        // Subagent tools: only for main agent (prevents recursion)
        if (!isSubAgent)
        {
            tools.Add(SubAgentTool.AsSubAgentRunFunction(_subAgentManager, _agentManager, _scopeFactory, _agentStore, _blockStore, _llmOpts, sessionId));
            tools.Add(SubAgentTool.AsSubAgentUseFunction(_subAgentManager, _agentManager, _scopeFactory, _agentStore, _blockStore, _llmOpts, sessionId));
            tools.Add(SubAgentTool.AsSubAgentListFunction(_subAgentManager, _agentStore, _blockStore, sessionId));
        }

        return tools;
    }
}
