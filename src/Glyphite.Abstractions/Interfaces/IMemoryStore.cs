using Glyphite.Abstractions.Models;

namespace Glyphite.Abstractions.Interfaces;

public interface IMemoryStore : IAgentStore, IBlockStore, IConfigStore, IDisposable
{
}
