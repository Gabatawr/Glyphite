using System.Collections.Generic;
using System.Threading.Tasks;

namespace Glyphite.Abstractions.Interfaces;

public interface ISubAgentConfigLoader
{
    Task LoadConfigAsync(string agentId, string agentCwd, string parentCwd);
}
