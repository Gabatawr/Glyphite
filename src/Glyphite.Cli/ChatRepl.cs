using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Cli.Services;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Serilog;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private readonly IConfigService _cfgService;
    private readonly SessionManager _session;
    private readonly InputHistory _inputHistory;
    private readonly ConsoleRenderer _renderer;
    private readonly CompactionService _compactionService;

    // LLM config — refreshed each turn via UpdatePromptPrefixAsync
    private int _contextWindow;
    private LlmModel[] _models = [];

    // Usage state — updated by streaming, read by prompt rendering
    private long _lastTurnHit;
    private long _lastTurnMiss;
    private long _lastTurnOutput;
    private long _lastTurnLastHit;
    private long _lastTurnLastMiss;
    private double _prevCumulativeCost = -1;

    // Convenience accessors
    private ITurnProcessor TurnProcessor => _session.TurnProcessor;
    private IBlockMemoryProvider BlockMemory => _session.BlockMemory;
    private string AgentId => _session.AgentId;

    public ChatRepl(
        IAgentStore agentStore,
        IBlockStore blockStore,
        IConfigService cfgService,
        SessionManager session,
        InputHistory inputHistory,
        ConsoleRenderer renderer,
        CompactionService compactionService)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _cfgService = cfgService;
        _session = session;
        _inputHistory = inputHistory;
        _renderer = renderer;
        _compactionService = compactionService;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var cwd = Directory.GetCurrentDirectory();
        var result = await _session.CreateOrResumeAgentAsync(cwd);

        if (result == CreateOrResumeResult.NeedsUserChoice)
            await _session.HandleUserChoiceAsync(cwd);

        var usage = await _session.ResetSessionStateAsync();
        (_lastTurnHit, _lastTurnMiss, _lastTurnOutput, _lastTurnLastHit, _lastTurnLastMiss) = usage;
        await UpdatePromptPrefixAsync();

        var chatOptions = await _session.InitializeAfterAgentAsync(cwd);

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
