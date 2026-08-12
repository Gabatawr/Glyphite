using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

public partial class TurnProcessor : ITurnProcessor
{
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private readonly IBlockMemoryProvider _blockMemory;
    private readonly IChatClient _chatClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly IConfigService _cfgService;
    private readonly CompactionService _compactionService;
    private readonly ILogger _logger;
    private readonly ISessionConfigLoader _configLoader;
    private readonly IInstructionProvider _instructionProvider;

    /// <summary>Last iteration's usage snapshot — prompt fallback for ChatRepl after Escape/crash.</summary>
    public UsageSnapshot? LastIterationUsage { get; private set; }

    public TurnProcessor(
        IAgentStore agentStore,
        IBlockStore blockStore,
        IBlockMemoryProvider blockMemory,
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        IConfigService cfgService,
        CompactionService compactionService,
        ISessionConfigLoader configLoader,
        IInstructionProvider instructionProvider,
        ILogger<TurnProcessor> logger)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _blockMemory = blockMemory;
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _cfgService = cfgService;
        _compactionService = compactionService;
        _configLoader = configLoader;
        _instructionProvider = instructionProvider;
        _logger = logger;
    }

    /// <summary>Wires the turn pipeline: prepare → compact → stream → finalize.</summary>
    public async IAsyncEnumerable<TurnEvent> ProcessAsync(
        string agentId,
        string input,
        ChatOptions chatOptions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        string? agentCwd = null)
    {
        var (prep, error) = await PrepareAsync(agentId, input, chatOptions, agentCwd);
        if (prep is null)
        {
            yield return new TurnErrorEvent(error!);
            yield break;
        }

        await foreach (var e in CompactIfNeededAsync(prep, ct))
            yield return e;

        await foreach (var e in StreamAsync(prep, ct))
            yield return e;

        await foreach (var e in FinalizeAsync(prep))
            yield return e;
    }
}
