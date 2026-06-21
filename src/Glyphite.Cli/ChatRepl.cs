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
    private ITurnProcessor _turnProcessor = null!;
    private IBlockMemoryProvider _blockMemory = null!;
    private readonly IConfigService _cfgService;
    private readonly IAgentManager _agentManager;
    private readonly IAgentScopeFactory _scopeFactory;
    private readonly ConsoleRenderer _renderer;
    private readonly DeepSeekOptions _deepseek;
    private readonly AgentOptions _agentOpts;
    private readonly CompressionOptions _compressionOpts;
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
        IOptions<AgentOptions> agentOpts,
        IOptions<CompressionOptions> compressionOpts)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _cfgService = cfgService;
        _agentManager = agentManager;
        _scopeFactory = scopeFactory;
        _renderer = renderer;
        _deepseek = deepseek.Value;
        _agentOpts = agentOpts.Value;
        _compressionOpts = compressionOpts.Value;
        // Scoped services — resolved lazily from AgentScope
        _turnProcessor = null!;
        _blockMemory = null!;
    }

    /// <summary>Switch to a new agent scope. Call when creating/switching/cloning agents.</summary>
    private void SwitchScope()
    {
        _currentScope?.Dispose();
        _currentScope = _scopeFactory.CreateScope();
        _turnProcessor = _currentScope.TurnProcessor;
        _blockMemory = _currentScope.BlockMemoryProvider;
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