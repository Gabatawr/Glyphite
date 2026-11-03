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
        IReadOnlyList<ImagePayload> Attachments,
        string AgentCwd)
    {
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

        // Apply reasoning effort from config (None = suppress, null = let provider decide).
        // `chatOptions` is created once per session and reused every turn, so the "absent" case must
        // clear what an earlier turn set — otherwise a removed setting keeps working.
        if (llmOpts.ReasoningEffort is { } effortStr
            && Enum.TryParse<ReasoningEffort>(effortStr, ignoreCase: true, out var parsedEffort))
        {
            chatOptions.Reasoning ??= new ReasoningOptions();
            chatOptions.Reasoning.Effort = parsedEffort;
        }
        else
        {
            chatOptions.Reasoning = null;
        }

        // The agent's own model (whatever `/models` stored) wins over the config default — but both are
        // re-read every turn. `chatOptions` lives for the whole session, so keeping the model there froze
        // it at session start: neither a new pick nor an edited `LLM:Model` ever reached the request.
        chatOptions.ModelId = await _agentStore.GetAgentModelAsync(agentId) ?? llmOpts.Model;
        var modelStr = chatOptions.ModelId;
        _logger.LogInformation("Turn start session {SessionId}, model {Model}", agentId, modelStr);

        // ── Build system instructions (system-prompt.md + AGENTS.md + Glyphite.{agentId}.md) ──
        var homePath = await _agentStore.GetAgentHomePathAsync(agentId);
        chatOptions.Instructions = await _instructionProvider.BuildInstructionsAsync(
            agentId, homePath, parentCwd, agentCwd);

        var isEphemeral = chatOptions.AdditionalProperties?.TryGetValue("ephemeral", out var epVal) == true
            && string.Equals(epVal as string, "true", StringComparison.OrdinalIgnoreCase);
        var isSubagent = chatOptions.AdditionalProperties?.ContainsKey("isSubagent") == true;
        // Tools resolve relative paths against the agent's own directory: a subagent gets the cwd it
        // was created with, a CLI agent its home. Without this they fell back to the process cwd, so
        // a subagent told cwd=/other/project wrote files into the directory glyphite was launched from.
        chatOptions.Tools = (await _toolRegistry.GetBuiltinToolsAsync(agentId, !isEphemeral, agentCwd)).ToList();

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
            agentOpts, llmOpts, compOpts, contextMessages, initialMessages, attachments, agentCwd), null);
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
                    await _blockStore.AppendBlocksAsync(prep.AgentId,
                        [MemoryBlock.AutoTool("compression", compactArgs, "", prep.ModelStr)]);
                }
            }
        }

        // ── Auto-compact large reasoning blocks from previous turns ──
        if (prep.CompOpts.AutoCompressReasoning)
        {
            var allReasoning = await _blockStore.LoadBlocksByTypeAsync(prep.AgentId, BlockType.agent_reasoning, null, false);
            var oversized = allReasoning
                .Where(b => !b.Compressed && b.Content.Length > prep.CompOpts.AutoCompressReasoningMaxSize)
                .ToList();

            // Reasoning is kept as history on purpose (it holds decisions that never made it into
            // the answer), so the only brake on its size is this compression — and it must not
            // touch the newest turn. That reasoning is the model's working memory for the next
            // step, and rewriting it would edit the very context this turn was planned on. Older
            // turns are fair game, the same split compaction uses for its zones.
            var toCompress = oversized;
            if (oversized.Count > 0)
            {
                var lastTurnStart = await GetLastTurnStartNumberAsync(prep.AgentId);
                toCompress = oversized.Where(b => b.Number <= lastTurnStart).ToList();
            }

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
                                    var (hit, miss, output, reasoning) = UsageParser.Parse(doc);
                                    if (hit > 0 || miss > 0 || output > 0)
                                        await _agentStore.RecordUsageAsync(prep.AgentId, hit, miss, output, reasoning, model: prep.ModelStr);
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

    /// <summary>
    /// Number of the block that opens the newest turn — the last <c>user_message</c> or
    /// <c>agent_task</c> (subagents start their turns with the latter). Blocks at or below it belong
    /// to older turns. A session with neither has no identifiable turn boundary, so everything stays
    /// eligible — the behaviour before the newest-turn rule existed.
    /// </summary>
    private async Task<double> GetLastTurnStartNumberAsync(string agentId)
    {
        var lastUser = await _blockStore.LoadBlocksByTypeAsync(agentId, BlockType.user_message, 1, desc: true);
        var lastTask = await _blockStore.LoadBlocksByTypeAsync(agentId, BlockType.agent_task, 1, desc: true);

        return lastUser.Concat(lastTask)
            .Select(b => b.Number)
            .DefaultIfEmpty(double.MaxValue)
            .Max();
    }

    /// <summary>Phase 3 — stream the LLM response, persisting blocks and yielding UI events.</summary>
    private async IAsyncEnumerable<TurnEvent> StreamAsync(
        PreparedTurn prep, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var agentClient = new AgentChatClient(_chatClient, prep.AgentId, prep.ModelStr);
        var failSafeClient = new FailSafeChatClient(
            agentClient, prep.AgentOpts.MaxToolIterations, _logger, _imageSink);

        // Subscribe: write per-iteration usage immediately — survives crash/Escape
        failSafeClient.OnIterationRecorded = (hit, miss, output, reasoning) =>
        {
            // Save for ChatRepl fallback (used when UsageTurnEvent doesn't arrive due to Escape)
            LastIterationUsage = SnapshotFrom(failSafeClient);
            return _agentStore.RecordUsageAsync(prep.AgentId, hit, miss, output, reasoning, hit, miss, prep.ModelStr);
        };

        _blockMemory.CurrentExecutedIds.Value = failSafeClient.ExecutedCallIds;

        var storedInput = WithAttachmentMarker(prep.Input, prep.Attachments);

        var userBlock = prep.IsSubagent
            ? MemoryBlock.AgentTask(storedInput)
            : MemoryBlock.UserMessage(storedInput);
        await _blockStore.AppendBlocksAsync(prep.AgentId, [userBlock]);

        // write_file results are re-read from disk in the streaming pipeline — carry the same
        // MaxSize limit that ToolConfigDecorator applies, so the re-read content can't blow up context.
        var writeFileMaxSize = prep.ChatOptions.Tools?
            .OfType<ToolConfigDecorator>()
            .FirstOrDefault(t => string.Equals(t.Name, "write_file", StringComparison.OrdinalIgnoreCase))
            ?.ContentMaxSize ?? ToolExecutionDefaults.ContentMaxSize;

        var ctx = new TurnContext(
            _blockStore, _agentStore, _logger,
            prep.AgentId, prep.ModelStr,
            failSafeClient, writeFileMaxSize, prep.AgentCwd, prep.ChatOptions.Tools as IReadOnlyList<AITool>);
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

        // ...and mirror that on the in-memory copies: peek results are wiped from the store right
        // above, so a size that still counted them would overstate what the history keeps.
        var storedBlocks = ctx.StoredBlocks;
        foreach (var peek in storedBlocks.Where(b => b.Data?.ContainsKey("peek") == true))
            peek.ToolResult = null;

        // Insert turn marker block. Three rulers, deliberately kept apart:
        //   out_ / reasoning — what the provider billed. Reasoning is generated and paid for as
        //                      output, but the provider returns only part of it (and old reasoning
        //                      gets rewritten by auto-compression), so billing is always larger.
        //   context          — the prompt of the LAST iteration: the size the conversation actually
        //                      had. `hit`/`miss` above are summed over every iteration of the turn
        //                      (a 4-iteration turn bills ~4x its context), so neither of them answers
        //                      "how big is the context" — this does, and it matches what the
        //                      compaction threshold reads from `session_usage.last_request_*`.
        //   stored*          — how much this turn actually occupies in the history, measured from the
        //                      blocks themselves with the ruler compaction uses. The three parts add
        //                      up to `stored`, so a reasoning block's size is never mixed up with the
        //                      size of an answer, and neither with the bill.
        var usage = SnapshotFrom(client);
        var stored = CompactionService.EstimateZoneTokens(storedBlocks);
        var storedReasoning = CompactionService.EstimateZoneTokens(
            storedBlocks.Where(b => b.Type == BlockType.agent_reasoning).ToList());
        var storedText = CompactionService.EstimateZoneTokens(
            storedBlocks.Where(b => b.Type == BlockType.agent_message).ToList());
        var storedTool = CompactionService.EstimateZoneTokens(
            storedBlocks.Where(b => b.Type == BlockType.tool).ToList());
        var turnBlock = MemoryBlock.TurnMarker(
            JsonSerializer.Serialize(new
            {
                hit = usage.TotalHit,
                miss = usage.TotalMiss,
                out_ = usage.TotalOutput,
                reasoning = usage.TotalReasoning,
                context = usage.LastHit + usage.LastMiss,
                stored,
                stored_reasoning = storedReasoning,
                stored_text = storedText,
                stored_tool = storedTool
            }));
        await _blockStore.AppendBlocksAsync(prep.AgentId, [turnBlock]);

        yield return new TurnCompleteEvent();
    }

    /// <summary>Build an immutable usage snapshot from the fail-safe client's tracker.</summary>
    private static UsageSnapshot SnapshotFrom(FailSafeChatClient client)
        => new(
            client.TotalCacheHitTokens, client.TotalCacheMissTokens, client.TotalOutputTokens,
            client.LastHitTokens, client.LastMissTokens, client.TotalReasoningTokens);
}
