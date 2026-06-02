using Glyphite.Host.Data;
using Glyphite.Host.Memory;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class ToolFactory
{
    public static List<AITool> CreateBuiltinTools(
        BashSessionManager bashManager,
        string sessionId,
        ConfigService cfgService,
        string defaultDir,
        MemoryStore memoryStore,
        BlockMemoryProvider blockMemory,
        HttpClient httpClient)
    {
        return
        [
            BashTool.AsAIFunction(bashManager, sessionId, cfgService),
            FileReadTool.AsAIFunction(cfgService, defaultDir),
            FileWriteTool.AsAIFunction(memoryStore, sessionId, defaultDir),
            FilePatchTool.AsAIFunction(defaultDir),
            MemoryTool.AsAIFunction(blockMemory, sessionId),
            TodoTool.AsTodoWriteFunction(memoryStore, sessionId, cfgService),
            TodoTool.AsTodoUpdateFunction(memoryStore, sessionId, cfgService),
            WebFetchTool.AsFetchFunction(cfgService, httpClient: httpClient),
            SearchTools.AsGlobFunction(cfgService, defaultDir),
            SearchTools.AsGrepFunction(cfgService, defaultDir),
        ];
    }
}
