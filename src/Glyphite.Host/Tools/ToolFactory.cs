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
        BlockMemoryProvider blockMemory)
    {
        return
        [
            BashTool.AsAIFunction(bashManager, sessionId, cfgService),
            FileReadTool.AsAIFunction(cfgService, defaultDir, sessionId),
            FileWriteTool.AsAIFunction(memoryStore, sessionId, defaultDir),
            FilePatchTool.AsAIFunction(defaultDir),
            MemoryTool.AsAIFunction(blockMemory, sessionId),
            TodoTool.AsTodoWriteFunction(memoryStore, sessionId, cfgService),
            TodoTool.AsTodoUpdateFunction(memoryStore, sessionId, cfgService),
            WebFetchTool.AsFetchFunction(cfgService, sessionId),
            SearchTools.AsGlobFunction(cfgService, defaultDir, sessionId),
            SearchTools.AsGrepFunction(cfgService, defaultDir, sessionId),
        ];
    }
}
