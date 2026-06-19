using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface IToolRegistry
{
    IReadOnlyList<AITool> GetBuiltinTools(string sessionId, bool includeMemory = false);
}
