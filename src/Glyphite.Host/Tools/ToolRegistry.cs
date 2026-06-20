using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Glyphite.Host.Tools;

public class ToolRegistry : IToolRegistry
{
    private readonly IBashSessionManager _bashManager;
    private readonly IConfigService _cfgService;
    private readonly ISubAgentConfigLoader _configLoader;
    private readonly IMemoryStore _memoryStore;
    private readonly IBlockMemoryProvider _blockMemory;
    private readonly SubAgentManager _subAgentManager;
    private readonly IAgentManager _agentManager;
    private readonly IAgentScopeFactory _scopeFactory;
    private readonly IOptions<DeepSeekOptions> _deepseekOpts;
    private readonly string _defaultDir;

    public ToolRegistry(
        IBashSessionManager bashManager,
        IConfigService cfgService,
        ISubAgentConfigLoader configLoader,
        IMemoryStore memoryStore,
        IBlockMemoryProvider blockMemory,
        SubAgentManager subAgentManager,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IOptions<DeepSeekOptions> deepseekOpts)
    {
        _bashManager = bashManager;
        _cfgService = cfgService;
        _configLoader = configLoader;
        _memoryStore = memoryStore;
        _blockMemory = blockMemory;
        _subAgentManager = subAgentManager;
        _agentManager = agentManager;
        _scopeFactory = scopeFactory;
        _deepseekOpts = deepseekOpts;
        _defaultDir = Directory.GetCurrentDirectory();
    }

    public IReadOnlyList<AITool> GetBuiltinTools(string sessionId, bool includeMemory = false)
    {
        // Don't give subagent tools to subagents themselves — prevents recursive creation chaos
        var isSubAgent = _subAgentManager.Exists(sessionId);

        var tools = new List<AITool>
        {
            BashTool.AsAIFunction(_bashManager, sessionId, _cfgService),
            FileReadTool.AsAIFunction(_cfgService, _defaultDir, sessionId),
            FileWriteTool.AsAIFunction(_memoryStore, sessionId, _defaultDir),
            FilePatchTool.AsAIFunction(_defaultDir),
            TodoTool.AsTodoWriteFunction(_memoryStore, sessionId, _cfgService),
            TodoTool.AsTodoUpdateFunction(_memoryStore, sessionId, _cfgService),
            WebFetchTool.AsFetchFunction(_cfgService, sessionId),
            SearchTools.AsGlobFunction(_cfgService, _defaultDir, sessionId),
            SearchTools.AsGrepFunction(_cfgService, _defaultDir, sessionId),
        };

        // Memory tool: available for main agent, or for subagents with saveMemory=true
        if (!isSubAgent || includeMemory)
            tools.Add(MemoryTool.AsAIFunction(_blockMemory, sessionId, _cfgService));

        // Subagent tools: only for main agent (prevents recursion)
        if (!isSubAgent)
        {
            tools.Add(SubAgentTool.AsSubAgentRunFunction(_subAgentManager, _agentManager, _scopeFactory, _memoryStore, _configLoader, _deepseekOpts, sessionId));
            tools.Add(SubAgentTool.AsSubAgentUseFunction(_subAgentManager, _agentManager, _scopeFactory, _memoryStore, _configLoader, _deepseekOpts, sessionId));
            tools.Add(SubAgentTool.AsSubAgentListFunction(_subAgentManager, _memoryStore, sessionId));
        }

        return tools;
    }
}