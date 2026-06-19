using Glyphite.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Glyphite.Host.DI;

/// <summary>Wraps a DI scope for a single agent session. Dispose to release scoped services.</summary>
public sealed class AgentScope : IDisposable
{
    private readonly IServiceScope _scope;

    public AgentScope(IServiceScope scope)
    {
        _scope = scope;
    }

    public ITurnProcessor TurnProcessor => _scope.ServiceProvider.GetRequiredService<ITurnProcessor>();
    public IBlockMemoryProvider BlockMemoryProvider => _scope.ServiceProvider.GetRequiredService<IBlockMemoryProvider>();
    public IToolRegistry ToolRegistry => _scope.ServiceProvider.GetRequiredService<IToolRegistry>();

    public void Dispose() => _scope.Dispose();
}

/// <summary>Creates new AgentScope instances for agent sessions (main or sub-agent). Singleton.</summary>
public sealed class AgentScopeFactory : IAgentScopeFactory
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentScopeFactory(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public AgentScope CreateScope()
    {
        return new AgentScope(_scopeFactory.CreateScope());
    }
}

public interface IAgentScopeFactory
{
    AgentScope CreateScope();
}
