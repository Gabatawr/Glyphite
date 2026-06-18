using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Cli.Services;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private readonly IConfiguration _config;
    private readonly IMemoryStore _store;
    private readonly IBlockMemoryProvider _blockMemory;
    private readonly ITurnProcessor _turnProcessor;
    private readonly IConfigService _cfgService;
    private readonly IAgentManager _agentManager;
    private readonly ConsoleRenderer _renderer;
    private readonly DeepSeekOptions _deepseek;
    private readonly AgentOptions _agentOpts;
    private readonly ToolStreamingOptions _streamOpts;
    private readonly CompressionOptions _compressionOpts;
    private string _agentId = string.Empty;
    private long _lastTurnHit;
    private long _lastTurnMiss;
    private long _lastTurnOutput;
    private long _lastTurnLastHit;
    private long _lastTurnLastMiss;

    public ChatRepl(
        IConfiguration config,
        IMemoryStore store,
        IBlockMemoryProvider blockMemory,
        ITurnProcessor turnProcessor,
        IConfigService cfgService,
        IAgentManager agentManager,
        ConsoleRenderer renderer,
        IOptions<DeepSeekOptions> deepseek,
        IOptions<AgentOptions> agentOpts,
        IOptions<ToolStreamingOptions> streamOpts,
        IOptions<CompressionOptions> compressionOpts)
    {
        _config = config;
        _store = store;
        _blockMemory = blockMemory;
        _turnProcessor = turnProcessor;
        _cfgService = cfgService;
        _agentManager = agentManager;
        _renderer = renderer;
        _deepseek = deepseek.Value;
        _agentOpts = agentOpts.Value;
        _streamOpts = streamOpts.Value;
        _compressionOpts = compressionOpts.Value;
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
            try { await cancelMonitor; } catch { }
            Console.WriteLine();
            Console.WriteLine();
            await UpdatePromptPrefixAsync();
        }

        Log.CloseAndFlush();
    }
}