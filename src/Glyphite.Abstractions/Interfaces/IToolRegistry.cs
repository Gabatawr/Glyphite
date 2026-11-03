using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface IToolRegistry
{
    /// <summary>
    /// Builtin tools for one agent. <paramref name="baseDir"/> is the directory relative file and
    /// search paths resolve against — the agent's own working directory, which for a subagent is
    /// the <c>cwd</c> it was created with, not the process's current directory.
    /// Null falls back to the process default.
    /// </summary>
    Task<IReadOnlyList<AITool>> GetBuiltinToolsAsync(string agentId, bool includeMemory = false, string? baseDir = null);
}
