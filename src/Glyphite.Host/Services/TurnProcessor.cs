using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

public class TurnProcessor : ITurnProcessor
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

    // Last per-iteration usage (for ChatRepl fallback after Escape/crash)
    public long LastIterationTotalHit { get; private set; }
    public long LastIterationTotalMiss { get; private set; }
    public long LastIterationTotalOutput { get; private set; }
    public long LastIterationLastHit { get; private set; }
    public long LastIterationLastMiss { get; private set; }

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

    public async IAsyncEnumerable<TurnEvent> ProcessAsync(
        string agentId,
        string input,
        ChatOptions chatOptions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        string? agentCwd = null)
    {
        // ── 1. PREPARATION: config, options, context ──

        var parentCwd = Directory.GetCurrentDirectory();
        agentCwd ??= await _agentStore.GetAgentHomePathAsync(agentId) ?? parentCwd;
        await _configLoader.LoadConfigAsync(agentId, agentCwd, parentCwd);

        var llmOpts = await _cfgService.GetOptionsAsync<LlmOptions>(LlmOptions.Section, agentId);
        var agentOpts = await _cfgService.GetOptionsAsync<AgentOptions>(AgentOptions.Section, agentId);

        // Apply reasoning effort from config (None = suppress, null = let provider decide)
        if (llmOpts.ReasoningEffort is { } effortStr
            && Enum.TryParse<ReasoningEffort>(effortStr, ignoreCase: true, out var parsedEffort))
        {
            chatOptions.Reasoning ??= new ReasoningOptions();
            chatOptions.Reasoning.Effort = parsedEffort;
        }

        var modelStr = chatOptions.ModelId ?? llmOpts.Model;
        _logger.LogInformation("Turn start session {SessionId}, model {Model}", agentId, modelStr);

        // ── Build system instructions (system-prompt.md + AGENTS.md + Glyphite.{agentId}.md) ──
        var homePath = await _agentStore.GetAgentHomePathAsync(agentId);
        chatOptions.Instructions = await _instructionProvider.BuildInstructionsAsync(
            agentId, homePath, parentCwd, agentCwd);

        var nextNum = await _agentStore.GetNextNumberAsync(agentId);
        if (nextNum <= 0) nextNum = 1;

        // Auto-compaction: skip for ephemeral agents (subagent_run — transient, no benefit)
        var isEphemeral = chatOptions.AdditionalProperties?.ContainsKey("ephemeral") == true;

        if (!isEphemeral)
        {
            // 1. Fast check (no LLM) — yield event immediately so UI doesn't freeze
            var shouldCompact = await _compactionService.ShouldCompactAsync(agentId, llmOpts.ContextWindow);

            if (shouldCompact)
            {
                var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, agentId);
                var pickedStrategy = CompactionService.PickStrategy(compOpts);
                var compactArgs = JsonSerializer.Serialize(new { AutoCompress = true, AutoThreshold = compOpts.AutoThreshold, Strategy = pickedStrategy });

                // Yield to UI BEFORE summarization (avoids freeze)
                yield return new AutoToolTurnEvent("compression", compactArgs, false, "");

                // 2. Actual compaction (slow — LLM summarization)
                var compacted = await _compactionService.CompactAsync(agentId, llmOpts.ContextWindow);

                if (compacted)
                {
                    var compactBlock = MemoryBlock.AutoTool("compression", compactArgs, "", modelStr);
                    compactBlock.Number = nextNum++;
                    await _blockStore.AppendBlocksAsync(agentId, [compactBlock], nextNum);
                }
            }
        }

        var isSubagent = chatOptions.AdditionalProperties?.ContainsKey("isSubagent") == true;
        chatOptions.Tools = (await _toolRegistry.GetBuiltinToolsAsync(agentId, !isEphemeral)).ToList();

        var peekStats = await _blockStore.GetPeekBlockStatsAsync(agentId);
        var peekCleaned = await _blockStore.RemovePeekBlocksAsync(agentId);

        var contextMessages = await _blockMemory.BuildContextAsync(
            agentId, modelStr, llmOpts.ContextWindow);

        if (peekCleaned > 0)
        {
            var peekMsg = TurnContext.BuildPeekCleanMessage(peekCleaned, peekStats);
            var cleanArgs = JsonSerializer.Serialize(new { count = peekCleaned });
            yield return new AutoToolTurnEvent("peek_reasoning", cleanArgs, false, "");

            var autoBlock = MemoryBlock.AutoTool("peek_reasoning", cleanArgs, peekMsg, modelStr);
            autoBlock.Number = nextNum++;
            await _blockStore.AppendBlocksAsync(agentId, [autoBlock], nextNum);
        }

        var initialMessages = new List<ChatMessage>();
        initialMessages.AddRange(contextMessages);
        initialMessages.Add(new ChatMessage(ChatRole.User, input));

        var sessionClient = new SessionChatClient(_chatClient, agentId, modelStr);
        var failSafeClient = new FailSafeChatClient(
            sessionClient, agentOpts.MaxToolIterations, _logger);

        // Subscribe: write per-iteration usage immediately — survives crash/Escape
        failSafeClient.OnIterationRecorded = (hit, miss, output) =>
        {
            // Save for ChatRepl fallback (used when UsageTurnEvent doesn't arrive due to Escape)
            LastIterationTotalHit = failSafeClient.TotalCacheHitTokens;
            LastIterationTotalMiss = failSafeClient.TotalCacheMissTokens;
            LastIterationTotalOutput = failSafeClient.TotalOutputTokens;
            LastIterationLastHit = failSafeClient.LastHitTokens;
            LastIterationLastMiss = failSafeClient.LastMissTokens;
            return _agentStore.RecordUsageAsync(agentId, hit, miss, output, hit, miss, modelStr);
        };

        _blockMemory.CurrentExecutedIds.Value = failSafeClient.ExecutedCallIds;

        var userBlock = isSubagent ? MemoryBlock.AgentTask(input) : MemoryBlock.UserMessage(input);
        userBlock.Number = nextNum++;
        await _blockStore.AppendBlocksAsync(agentId, [userBlock], nextNum);

        // ── 2. STREAMING: create TurnContext and process updates ──

        var ctx = new TurnContext(
            _blockStore, _agentStore, _logger,
            agentId, modelStr, nextNum,
            contextMessages, failSafeClient, agentOpts);

        await foreach (var update in failSafeClient
            .GetStreamingResponseAsync(initialMessages, chatOptions)
            .WithCancellation(ct))
        {
            var events = await ctx.ProcessUpdate(update);
            foreach (var e in events)
                yield return e;
        }

        // ── 3. FINISH: flush remaining, cleanup ──

        // Usage already written per-iteration via OnIterationRecorded — no batch write needed.
        yield return new UsageTurnEvent(
            ctx.FailSafeClient.TotalCacheHitTokens,
            ctx.FailSafeClient.TotalCacheMissTokens,
            ctx.FailSafeClient.TotalOutputTokens,
            ctx.FailSafeClient.LastHitTokens,
            ctx.FailSafeClient.LastMissTokens);

        await ctx.FlushAll(ctx.AgentOpts);

        _logger.LogInformation("Turn end session {SessionId}: hit={Hit} miss={Miss} out={Output} lastHit={LastHit} lastMiss={LastMiss}",
            agentId,
            ctx.FailSafeClient.TotalCacheHitTokens,
            ctx.FailSafeClient.TotalCacheMissTokens,
            ctx.FailSafeClient.TotalOutputTokens,
            ctx.FailSafeClient.LastHitTokens,
            ctx.FailSafeClient.LastMissTokens);

        // End-of-turn: clean peek markers on non-reasoning blocks (tool, auto_tool)
        await _blockStore.ClearPeekMarkersAsync(agentId, false);

        // Insert turn marker block with usage summary
        var turnBlock = MemoryBlock.TurnMarker(
            JsonSerializer.Serialize(new { hit = ctx.FailSafeClient.TotalCacheHitTokens, miss = ctx.FailSafeClient.TotalCacheMissTokens, out_ = ctx.FailSafeClient.TotalOutputTokens }));
        turnBlock.Number = ctx.NextNum++;
        await _blockStore.AppendBlocksAsync(agentId, [turnBlock], ctx.NextNum);

        yield return new TurnCompleteEvent();
    }
}
