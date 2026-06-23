using System.Threading.Tasks;

namespace Glyphite.Abstractions.Interfaces;

public interface IInstructionProvider
{
    /// <summary>Build merged instructions: system-prompt.md + AGENTS.md + Glyphite.{agentId}.md.</summary>
    Task<string> BuildInstructionsAsync(string agentId, string? homePath, string parentCwd, string agentCwd);
    void InvalidateCache(string agentId);
}
