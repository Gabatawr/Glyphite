using System.Collections.Generic;
using System.Threading.Tasks;

namespace Glyphite.Abstractions.Interfaces;

public interface ISessionConfigLoader
{
    Task LoadConfigAsync(string agentId, string agentCwd, string parentCwd);
}
