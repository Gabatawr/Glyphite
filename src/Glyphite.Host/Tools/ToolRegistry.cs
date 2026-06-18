using Glyphite.Abstractions.Interfaces;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public class ToolRegistry : IToolRegistry
{
    private readonly IBashSessionManager _bashManager;
    private readonly IConfigService _cfgService;
    private readonly IMemoryStore _memoryStore;
    private readonly IBlockMemoryProvider _blockMemory;
    private readonly string _defaultDir;

    public ToolRegistry(
        IBashSessionManager bashManager,
        IConfigService cfgService,
        IMemoryStore memoryStore,
        IBlockMemoryProvider blockMemory)
    {
        _bashManager = bashManager;
        _cfgService = cfgService;
        _memoryStore = memoryStore;
        _blockMemory = blockMemory;
        _defaultDir = Directory.GetCurrentDirectory();
    }

    public IReadOnlyList<AITool> GetBuiltinTools(string sessionId)
    {
        return
        [
            BashTool.AsAIFunction(_bashManager, sessionId, _cfgService),
            FileReadTool.AsAIFunction(_cfgService, _defaultDir),
            FileWriteTool.AsAIFunction(_memoryStore, sessionId, _defaultDir),
            FilePatchTool.AsAIFunction(_defaultDir),
            MemoryTool.AsAIFunction(_blockMemory, sessionId),
            TodoTool.AsTodoWriteFunction(_memoryStore, sessionId, _cfgService),
            TodoTool.AsTodoUpdateFunction(_memoryStore, sessionId, _cfgService),
            WebFetchTool.AsFetchFunction(_cfgService),
            SearchTools.AsGlobFunction(_cfgService, _defaultDir),
            SearchTools.AsGrepFunction(_cfgService, _defaultDir),
        ];
    }
}
