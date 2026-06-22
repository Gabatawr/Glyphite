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
    private readonly ISubAgentConfigLoader _configLoader;

    public TurnProcessor(
        IAgentStore agentStore,
        IBlockStore blockStore,
        IBlockMemoryProvider blockMemory,
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        IConfigService cfgService,
        CompactionService compactionService,
        ISubAgentConfigLoader configLoader,
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
        _logger = logger;
    }

    public async IAsyncEnumerable<TurnEvent> ProcessAsync(
        string sessionId,
        string input,
        ChatOptions chatOptions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // ── 1. PREPARATION: config, options, context ──

        var parentCwd = Directory.GetCurrentDirectory();
        var agentCwd = await _agentStore.GetAgentHomePathAsync(sessionId) ?? parentCwd;
        await _configLoader.LoadConfigAsync(sessionId, agentCwd, parentCwd);

        var deepseekOpts = await _cfgService.GetOptionsAsync<DeepSeekOptions>(DeepSeekOptions.Section, sessionId);
        var agentOpts = await _cfgService.GetOptionsAsync<AgentOptions>(AgentOptions.Section, sessionId);

        var modelStr = chatOptions.ModelId ?? deepseekOpts.Model;
        _logger.LogInformation("Turn start session {SessionId}, model {Model}", sessionId, modelStr);

        var nextNum = await _agentStore.GetNextNumberAsync(sessionId);
        if (nextNum <= 0) nextNum = 1;

        // Auto-compaction
        // 1. Fast check (no LLM) — yield event immediately so UI doesn't freeze
        var shouldCompact = await _compactionService.ShouldCompactAsync(sessionId, deepseekOpts.ContextWindow);

        if (shouldCompact)
        {
            var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, sessionId);
            var compactArgs = $"{{\"AutoCompress\":true,\"AutoThreshold\":{compOpts.AutoThreshold}}}";

            // Yield to UI BEFORE summarization (avoids freeze)
            yield return new AutoToolTurnEvent("compression", compactArgs, false, "");

            // 2. Actual compaction (slow — LLM summarization)
            var compacted = await _compactionService.CompactAsync(sessionId, deepseekOpts.ContextWindow);

            if (compacted)
            {
                var compactBlock = MemoryBlock.AutoTool("compression", compactArgs, "", modelStr);
                compactBlock.Number = nextNum++;
                await _blockStore.AppendBlocksAsync(sessionId, [compactBlock], nextNum);
            }
        }

        var includeMemory = chatOptions.AdditionalProperties?.ContainsKey("saveMemory") == true;
        chatOptions.Tools = (await _toolRegistry.GetBuiltinToolsAsync(sessionId, includeMemory)).ToList();

        var peekStats = await _blockStore.GetPeekBlockStatsAsync(sessionId);
        var peekCleaned = await _blockStore.RemovePeekBlocksAsync(sessionId);

        var contextMessages = await _blockMemory.BuildContextAsync(
            sessionId, modelStr, deepseekOpts.ContextWindow);

        if (peekCleaned > 0)
        {
            var peekMsg = TurnContext.BuildPeekCleanMessage(peekCleaned, peekStats);
            var cleanArgs = $"{{\"count\":{peekCleaned}}}";
            yield return new AutoToolTurnEvent("peek_reasoning", cleanArgs, false, "");

            var autoBlock = MemoryBlock.AutoTool("peek_reasoning", cleanArgs, peekMsg, modelStr);
            autoBlock.Number = nextNum++;
            await _blockStore.AppendBlocksAsync(sessionId, [autoBlock], nextNum);

            contextMessages.Add(new ChatMessage(ChatRole.System, autoBlock.ToContextString()));
        }

        var initialMessages = new List<ChatMessage>();
        initialMessages.AddRange(contextMessages);
        initialMessages.Add(new ChatMessage(ChatRole.User, input));

        var sessionClient = new SessionChatClient(_chatClient, sessionId);
        var failSafeClient = new FailSafeChatClient(
            sessionClient, agentOpts.MaxToolIterations, _logger);

        _blockMemory.CurrentExecutedIds.Value = failSafeClient.ExecutedCallIds;

        var userBlock = MemoryBlock.UserMessage(input);
        userBlock.Number = nextNum++;
        await _blockStore.AppendBlocksAsync(sessionId, [userBlock], nextNum);

        // ── 2. STREAMING: create TurnContext and process updates ──

        var ctx = new TurnContext(
            _blockStore, _agentStore, _logger,
            sessionId, modelStr, nextNum,
            contextMessages, failSafeClient, agentOpts);

        await foreach (var update in failSafeClient
            .GetStreamingResponseAsync(initialMessages, chatOptions)
            .WithCancellation(ct))
        {
            var events = await ctx.ProcessUpdate(update);
            foreach (var e in events)
                yield return e;
        }

        // ── 3. FINISH: persist usage, flush remaining, cleanup ──

        // Persist usage from all iterations
        await _agentStore.RecordUsageAsync(
            sessionId,
            ctx.FailSafeClient.TotalCacheHitTokens,
            ctx.FailSafeClient.TotalCacheMissTokens,
            ctx.FailSafeClient.TotalOutputTokens,
            ctx.FailSafeClient.LastHitTokens,
            ctx.FailSafeClient.LastMissTokens,
            model: modelStr);
        yield return new UsageTurnEvent(
            ctx.FailSafeClient.TotalCacheHitTokens,
            ctx.FailSafeClient.TotalCacheMissTokens,
            ctx.FailSafeClient.TotalOutputTokens,
            ctx.FailSafeClient.LastHitTokens,
            ctx.FailSafeClient.LastMissTokens);

        await ctx.FlushAll(ctx.AgentOpts);

        _logger.LogInformation("Turn end session {SessionId}: hit={Hit} miss={Miss} out={Output} lastHit={LastHit} lastMiss={LastMiss}",
            sessionId,
            ctx.FailSafeClient.TotalCacheHitTokens,
            ctx.FailSafeClient.TotalCacheMissTokens,
            ctx.FailSafeClient.TotalOutputTokens,
            ctx.FailSafeClient.LastHitTokens,
            ctx.FailSafeClient.LastMissTokens);

        // End-of-turn: clean peek markers on non-reasoning blocks (tool, auto_tool)
        await _blockStore.ClearPeekMarkersAsync(sessionId, false);

        // Insert turn marker block with usage summary
        var turnBlock = MemoryBlock.TurnMarker(
            $"{{\"hit\":{ctx.FailSafeClient.TotalCacheHitTokens},\"miss\":{ctx.FailSafeClient.TotalCacheMissTokens},\"out\":{ctx.FailSafeClient.TotalOutputTokens}}}");
        turnBlock.Number = ctx.NextNum++;
        await _blockStore.AppendBlocksAsync(sessionId, [turnBlock], ctx.NextNum);

        yield return new TurnCompleteEvent();
    }
}
