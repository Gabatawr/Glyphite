using System.Text.Json;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Host.Tools;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

public partial class TurnProcessor
{
    /// <summary>Per-turn state shared across ProcessAsync phases.</summary>
    private sealed record PreparedTurn(
        string AgentId,
        string Input,
        string ModelStr,
        bool IsEphemeral,
        bool IsSubagent,
        ChatOptions ChatOptions,
        AgentOptions AgentOpts,
        LlmOptions LlmOpts,
        CompressionOptions CompOpts,
        List<ChatMessage> ContextMessages,
        List<ChatMessage> InitialMessages,
        IReadOnlyList<ImagePayload> Attachments)
    {
        public double NextNum { get; set; }
        public FailSafeChatClient? FailSafeClient { get; set; }
        public TurnContext? Ctx { get; set; }
    }

    /// <summary>Phase 1 — load config, build options/instructions/context, produce the per-turn state.</summary>
    private async Task<(PreparedTurn? Turn, string? Error)> PrepareAsync(
        string agentId, string input, ChatOptions chatOptions, string? agentCwd, CancellationToken ct)
    {
        // A cancelled turn can leave images queued by tools. Drop them so they never
        // surface in an unrelated later turn.
        _imageSink.Clear();

        var parentCwd = Directory.GetCurrentDirectory();
        agentCwd ??= await _agentStore.GetAgentHomePathAsync(agentId) ?? parentCwd;
        await _configLoader.LoadConfigAsync(agentId, agentCwd, parentCwd);

        var llmOpts = await _cfgService.GetOptionsAsync<LlmOptions>(LlmOptions.Section, agentId);
        var agentOpts = await _cfgService.GetOptionsAsync<AgentOptions>(AgentOptions.Section, agentId);

        // Lazy ApiKey check — allow startup even without a key
        if (string.IsNullOrWhiteSpace(llmOpts.ApiKey))
            return (null, "LLM API key is not configured. Open Glyphite.json in the current directory and set LLM:ApiKey.");

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

        var isEphemeral = chatOptions.AdditionalProperties?.TryGetValue("ephemeral", out var epVal) == true
            && string.Equals(epVal as string, "true", StringComparison.OrdinalIgnoreCase);
        var isSubagent = chatOptions.AdditionalProperties?.ContainsKey("isSubagent") == true;
        chatOptions.Tools = (await _toolRegistry.GetBuiltinToolsAsync(agentId, !isEphemeral)).ToList();

        var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, agentId);

        var contextMessages = await _blockMemory.BuildContextAsync(
            agentId, modelStr, llmOpts.ContextWindow);

        var attachments = await ResolveAttachmentsAsync(agentId, input, agentCwd, ct);

        // Bodies and image counts are budgeted end to end: what already rides in the opening message
        // counts against whatever tools attach later in the same turn, and a tool asking for one of
        // those same pictures is answered "already attached" instead of sending a second copy.
        _imageSink.Seed(attachments);

        var initialMessages = new List<ChatMessage>();
        initialMessages.AddRange(contextMessages);
        initialMessages.Add(BuildUserMessage(input, attachments));

        return (new PreparedTurn(
            agentId, input, modelStr, isEphemeral, isSubagent, chatOptions,
            agentOpts, llmOpts, compOpts, contextMessages, initialMessages, attachments)
        {
            NextNum = nextNum
        }, null);
    }

    /// <summary>
    /// Load the images referenced by path or URL in the user's message.
    /// Only references that are certainly images qualify — the model should never receive a
    /// surprise attachment, and a broken path must not fail the turn.
    /// </summary>
    private async Task<IReadOnlyList<ImagePayload>> ResolveAttachmentsAsync(
        string agentId, string input, string? agentCwd, CancellationToken ct)
    {
        var opts = await _cfgService.GetOptionsAsync<ImageOptions>(ImageOptions.Section, agentId);
        if (!opts.Enabled || !opts.AutoAttach) return [];

        var specs = ImageLoader.ExtractSpecs(input, opts, agentCwd);
        if (specs.Count == 0) return [];

        var loaded = new List<ImagePayload>();
        long totalBytes = 0;

        // ExtractSpecs already caps the list at MaxImagesPerRequest, so no second cut is needed here.
        foreach (var spec in specs)
        {
            var (payload, error) = await _imageLoader.LoadAsync(spec, opts, agentCwd, ct: ct);
            if (payload is null)
            {
                _logger.LogWarning("Skipped image attachment {Spec}: {Error}", spec, error);
                continue;
            }

            // Keep the whole request inside the provider's body limit.
            if (payload.Inline && totalBytes + payload.Bytes > opts.MaxTotalBytes)
            {
                _logger.LogWarning("Skipped image attachment {Spec}: inline size budget exhausted", spec);
                continue;
            }

            totalBytes += payload.Bytes;
            loaded.Add(payload);
        }

        if (loaded.Count > 0)
            _logger.LogInformation("Attached {Count} image(s) to the user message", loaded.Count);

        return loaded;
    }

    /// <summary>
    /// User message = the text plus every attached image. Images are legal only in user messages
    /// (the provider rejects them in system/assistant/tool roles), and the transcript itself is
    /// built from system blocks — so this is the only place they can ride.
    /// </summary>
    private static ChatMessage BuildUserMessage(string input, IReadOnlyList<ImagePayload> attachments)
    {
        if (attachments.Count == 0)
            return new ChatMessage(ChatRole.User, input);

        var contents = new List<AIContent> { new TextContent(input) };
        foreach (var image in attachments)
            contents.Add(image.Content);

        return new ChatMessage(ChatRole.User, contents);
    }

    /// <summary>Marker written into the stored user block: cheap text instead of base64, and it
    /// tells later turns an image was there so the agent can re-open it with `view_image`.</summary>
    private static string WithAttachmentMarker(string input, IReadOnlyList<ImagePayload> attachments)
        => attachments.Count == 0
            ? input
            : input + "\n" + string.Join("\n", attachments.Select(a => $"[image attached: {a.Describe()}]"));

    /// <summary>Phase 2 — auto-compaction (session + reasoning) before streaming.</summary>
    private async IAsyncEnumerable<TurnEvent> CompactIfNeededAsync(
        PreparedTurn prep, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Auto-compaction: skip only for truly ephemeral agents (subagent_run — transient, no benefit).
        // subagent_use sets ephemeral=false so compaction runs normally.
        if (!prep.IsEphemeral)
        {
            var status = await _compactionService.EvaluateCompactionStatusAsync(prep.AgentId, prep.LlmOpts.ContextWindow);

            if (status.IsThresholdExceeded && status.WillCompact)
            {
                var compactArgs = JsonSerializer.Serialize(new
                {
                    AutoCompress = true,
                    Strategy = status.Strategy,
                    Mode = status.Mode
                });

                // Yield to UI BEFORE summarization (avoids freeze)
                yield return new AutoToolTurnEvent("compression", compactArgs, false, "");

                // Actual compaction (slow — LLM summarization)
                var compacted = await _compactionService.CompactAsync(prep.AgentId, prep.LlmOpts.ContextWindow, status.Strategy);

                if (compacted)
                {
                    var compactBlock = MemoryBlock.AutoTool("compression", compactArgs, "", prep.ModelStr);
                    compactBlock.Number = prep.NextNum++;
                    await _blockStore.AppendBlocksAsync(prep.AgentId, [compactBlock], prep.NextNum);
                }
            }
        }

        // ── Auto-compact large reasoning blocks from previous turn ──
        if (prep.CompOpts.AutoCompressReasoning)
        {
            var allReasoning = await _blockStore.LoadBlocksByTypeAsync(prep.AgentId, BlockType.agent_reasoning, null, false);
            var toCompress = allReasoning
                .Where(b => !b.Compressed && b.Content.Length > prep.CompOpts.AutoCompressReasoningMaxSize)
                .ToList();

            if (toCompress.Count > 0)
            {
                var maxOutputTokens = Math.Max(1, prep.CompOpts.AutoCompressReasoningMaxSize / 4 / 2);
                var compactedCount = 0;

                // Notify UI BEFORE compaction (explains why we're hanging)
                var compressArgs = JsonSerializer.Serialize(new { count = toCompress.Count, maxTokens = maxOutputTokens });
                yield return new AutoToolTurnEvent("compress_reasoning", compressArgs, false, "");

                var tasks = toCompress.Select(async block =>
                {
                    try
                    {
                        var messages = new List<ChatMessage>
                        {
                            new(ChatRole.System, "You are a precise reasoning summarizer. Extract the key insights, conclusions, and decisions from the following reasoning/thinking block. Keep only what matters — discard meandering exploration, false starts, and redundant thoughts. Be concise. Output in the same language as the original."),
                            new(ChatRole.User, block.Content)
                        };

                        var response = await _chatClient.GetResponseAsync(messages, new ChatOptions
                        {
                            MaxOutputTokens = maxOutputTokens,
                            ModelId = prep.ModelStr
                        }, ct);

                        var summary = response.Messages
                            .LastOrDefault(m => m.Role == ChatRole.Assistant)
                            ?.Text?.Trim();

                        if (!string.IsNullOrEmpty(summary) && summary.Length < block.Content.Length)
                        {
                            await _blockStore.UpdateBlockContentAsync(prep.AgentId, block.Number, summary);
                            Interlocked.Increment(ref compactedCount);
                        }

                        // Record usage
                        if (response.RawRepresentation is not null)
                        {
                            try
                            {
                                using var doc = UsageParser.Normalize(response.RawRepresentation);
                                if (doc is not null)
                                {
                                    var (hit, miss, output) = UsageParser.Parse(doc);
                                    if (hit > 0 || miss > 0 || output > 0)
                                        await _agentStore.RecordUsageAsync(prep.AgentId, hit, miss, output, model: prep.ModelStr);
                                }
                            }
                            catch { _logger.LogWarning("Failed to parse usage from reasoning compaction response"); }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to compact reasoning block {Number}", block.Number);
                    }
                });

                await Task.WhenAll(tasks);

                if (compactedCount > 0)
                {
                    _logger.LogInformation("Compacted {Count} reasoning blocks (max {MaxTokens} tok each)", compactedCount, maxOutputTokens);
                }
            }
        }
    }

    /// <summary>Phase 3 — stream the LLM response, persisting blocks and yielding UI events.</summary>
    private async IAsyncEnumerable<TurnEvent> StreamAsync(
        PreparedTurn prep, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var agentClient = new AgentChatClient(_chatClient, prep.AgentId, prep.ModelStr);
        var failSafeClient = new FailSafeChatClient(
            agentClient, prep.AgentOpts.MaxToolIterations, _logger, _imageSink);

        // Subscribe: write per-iteration usage immediately — survives crash/Escape
        failSafeClient.OnIterationRecorded = (hit, miss, output) =>
        {
            // Save for ChatRepl fallback (used when UsageTurnEvent doesn't arrive due to Escape)
            LastIterationUsage = SnapshotFrom(failSafeClient);
            return _agentStore.RecordUsageAsync(prep.AgentId, hit, miss, output, hit, miss, prep.ModelStr);
        };

        _blockMemory.CurrentExecutedIds.Value = failSafeClient.ExecutedCallIds;

        var storedInput = WithAttachmentMarker(prep.Input, prep.Attachments);

        var userBlock = prep.IsSubagent
            ? MemoryBlock.AgentTask(storedInput)
            : MemoryBlock.UserMessage(storedInput);
        userBlock.Number = prep.NextNum++;
        await _blockStore.AppendBlocksAsync(prep.AgentId, [userBlock], prep.NextNum);

        // write_file results are re-read from disk in the streaming pipeline — carry the same
        // MaxSize limit that ToolConfigDecorator applies, so the re-read content can't blow up context.
        var writeFileMaxSize = prep.ChatOptions.Tools?
            .OfType<ToolConfigDecorator>()
            .FirstOrDefault(t => string.Equals(t.Name, "write_file", StringComparison.OrdinalIgnoreCase))
            ?.ContentMaxSize ?? ToolExecutionDefaults.ContentMaxSize;

        var ctx = new TurnContext(
            _blockStore, _agentStore, _logger,
            prep.AgentId, prep.ModelStr, prep.NextNum,
            failSafeClient, writeFileMaxSize);
        prep.FailSafeClient = failSafeClient;
        prep.Ctx = ctx;

        await foreach (var update in failSafeClient
            .GetStreamingResponseAsync(prep.InitialMessages, prep.ChatOptions)
            .WithCancellation(ct))
        {
            var events = await ctx.ProcessUpdate(update);
            foreach (var e in events)
                yield return e;
        }
    }

    /// <summary>Phase 4 — flush remaining blocks, emit usage + turn marker, cleanup.</summary>
    private async IAsyncEnumerable<TurnEvent> FinalizeAsync(PreparedTurn prep)
    {
        var client = prep.FailSafeClient!;
        var ctx = prep.Ctx!;

        // Usage already written per-iteration via OnIterationRecorded — no batch write needed.
        yield return new UsageTurnEvent(SnapshotFrom(client));

        await ctx.FlushAll();

        _logger.LogInformation("Turn end session {SessionId}: hit={Hit} miss={Miss} out={Output} lastHit={LastHit} lastMiss={LastMiss}",
            prep.AgentId,
            client.TotalCacheHitTokens,
            client.TotalCacheMissTokens,
            client.TotalOutputTokens,
            client.LastHitTokens,
            client.LastMissTokens);

        // End-of-turn: clean peek markers on non-reasoning blocks (tool, auto_tool)
        await _blockStore.ClearPeekMarkersAsync(prep.AgentId, false);

        // Insert turn marker block with usage summary
        var usage = SnapshotFrom(client);
        var turnBlock = MemoryBlock.TurnMarker(
            JsonSerializer.Serialize(new { hit = usage.TotalHit, miss = usage.TotalMiss, out_ = usage.TotalOutput }));
        turnBlock.Number = ctx.NextNum++;
        await _blockStore.AppendBlocksAsync(prep.AgentId, [turnBlock], ctx.NextNum);

        yield return new TurnCompleteEvent();
    }

    /// <summary>Build an immutable usage snapshot from the fail-safe client's tracker.</summary>
    private static UsageSnapshot SnapshotFrom(FailSafeChatClient client)
        => new(
            client.TotalCacheHitTokens, client.TotalCacheMissTokens, client.TotalOutputTokens,
            client.LastHitTokens, client.LastMissTokens);
}
