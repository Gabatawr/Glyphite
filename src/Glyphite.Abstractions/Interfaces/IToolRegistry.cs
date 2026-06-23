using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface IToolRegistry
{
    Task<IReadOnlyList<AITool>> GetBuiltinToolsAsync(string agentId, bool includeMemory = false);
}
