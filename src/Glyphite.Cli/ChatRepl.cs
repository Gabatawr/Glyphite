using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Cli.Services;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Serilog;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private ITurnProcessor _turnProcessor => _currentScope?.TurnProcessor ?? throw new InvalidOperationException("Scope not initialized. SwitchScope() before use.");
    private IBlockMemoryProvider _blockMemory => _currentScope?.BlockMemoryProvider ?? throw new InvalidOperationException("Scope not initialized. SwitchScope() before use.");
    private readonly IConfigService _cfgService;
    private readonly IAgentManager _agentManager;
    private readonly IAgentScopeFactory _scopeFactory;
    private readonly ConsoleRenderer _renderer;
    private readonly DeepSeekOptions _deepseek;
    private readonly AgentOptions _agentOpts;
    private AgentScope? _currentScope;
    private string _agentId = string.Empty;
    private long _lastTurnHit;
    private long _lastTurnMiss;
    private long _lastTurnOutput;
    private long _lastTurnLastHit;
    private long _lastTurnLastMiss;
    private double _prevCumulativeCost = -1;

    public ChatRepl(
        IAgentStore agentStore,
        IBlockStore blockStore,
        IConfigService cfgService,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        ConsoleRenderer renderer,
        IOptions<DeepSeekOptions> deepseek,
        IOptions<AgentOptions> agentOpts)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _cfgService = cfgService;
        _agentManager = agentManager;
        _scopeFactory = scopeFactory;
        _renderer = renderer;
        _deepseek = deepseek.Value;
        _agentOpts = agentOpts.Value;
    }

    /// <summary>Switch to a new agent scope. Call when creating/switching/cloning agents.</summary>
    private void SwitchScope()
    {
        _currentScope?.Dispose();
        _currentScope = _scopeFactory.CreateScope();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var cwd = Directory.GetCurrentDirectory();
        await CreateOrResumeAgentAsync(cwd);
        var chatOptions = await InitializeAfterAgentAsync(cwd);

        while (!ct.IsCancellationRequested)
        {
            var input = await ReadLineWithHistoryAsync();
            if (input is null || input == "/exit")
                break;

            if (await HandleCommandAsync(input))
                continue;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            Console.WriteLine();

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var cancelMonitor = MonitorEscapeAsync(turnCts);
            await ProcessInputAsync(input, chatOptions, turnCts.Token);
            turnCts.Cancel(); // stop monitor
            try { await cancelMonitor; } catch { /* monitor cancelled */ }
            Console.WriteLine();
            Console.WriteLine();
            await UpdatePromptPrefixAsync();
        }

        Log.CloseAndFlush();
    }
}