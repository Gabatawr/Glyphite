using System.Runtime.CompilerServices;
using System.Text.Json;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

public sealed class FailSafeChatClient : DelegatingChatClient
{
    private readonly int _maxIterations;

    public HashSet<string> ExecutedCallIds { get; } = [];

    /// <summary>Tracks CallIds of peek=true tool calls for cleanup after LLM consumes them.</summary>
    private readonly HashSet<string> _pendingPeekCallIds = [];

    /// <summary>Tracks block numbers deleted by memory clean for removal from messageList after LLM consumes them.</summary>
    private readonly List<double> _pendingMemoryCleanBlocks = [];

    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
    public Action<long, long, long>? OnUsage { get; set; }

    /// <summary>Called after each batch of tool executions completes. Returns text to append to tool results (or null).</summary>
    public Func<Task<string?>>? OnBatchComplete { get; set; }

    public long TotalCacheHitTokens { get; private set; }
    public long TotalCacheMissTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }

    public long LastHitTokens { get; private set; }
    public long LastMissTokens { get; private set; }

    public FailSafeChatClient(IChatClient inner, int maxIterations) : base(inner)
    {
        _maxIterations = maxIterations;
    }

    // ── Parallel-safe tool names (can be grouped for concurrent execution) ──
    private static readonly HashSet<string> _parallelSafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "fetch_web", "search_glob", "search_grep", "subagent_use", "subagent_run"
    };

    /// <summary>
    /// Group consecutive parallel-safe tool calls into batches for concurrent execution.
    /// For subagent_use / subagent_run: mode="parallel" is parallel-safe
    /// (with duplicate agent name check — same name splits into sequential groups).
    /// mode="sequential" or no mode (default) stops the group.
    /// </summary>
    private static List<List<FunctionCallContent>> BuildToolGroups(IReadOnlyList<FunctionCallContent> fccs)
    {
        var groups = new List<List<FunctionCallContent>>();
        var current = new List<FunctionCallContent>();
        var agentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fcc in fccs)
        {
            var name = fcc.Name ?? "";
            var isSafe = _parallelSafeTools.Contains(name);

            if (!isSafe)
            {
                if (current.Count > 0) { groups.Add(current); current = []; agentNames.Clear(); }
                groups.Add([fcc]);
                continue;
            }

            // subagent_use / subagent_run: check mode and duplicate agent names
            if (name is "subagent_use" or "subagent_run")
            {
                // Check mode — default is "sequential"
                string? mode = null;
                if (fcc.Arguments?.TryGetValue("mode", out var modeObj) == true)
                    mode = modeObj?.ToString();
                var isParallelMode = string.Equals(mode, "parallel", StringComparison.OrdinalIgnoreCase);

                if (!isParallelMode)
                {
                    // Sequential mode: stop current group, add as sequential
                    if (current.Count > 0) { groups.Add(current); current = []; agentNames.Clear(); }
                    groups.Add([fcc]);
                    continue;
                }

                // Parallel mode: check for duplicate explicitly-provided agent names
                if (fcc.Arguments?.TryGetValue("name", out var nameObj) == true)
                {
                    var agentName = nameObj?.ToString();
                    if (!string.IsNullOrEmpty(agentName) && !agentNames.Add(agentName))
                    {
                        // Duplicate agent — flush current batch, start new one
                        groups.Add(current);
                        current = [];
                        agentNames.Clear();
                        agentNames.Add(agentName);
                    }
                }
            }

            current.Add(fcc);
        }

        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    /// <summary>Run a single tool and return (resultText, errorText, exception).</summary>
    private static async Task<(string? Result, string? Error, Exception? Exception)> RunToolAsync(
        AIFunction tool, IDictionary<string, object?>? args, CancellationToken ct)
    {
        try
        {
            var argsObj = args is not null ? new AIFunctionArguments(args) : null;
            var r = await tool.InvokeAsync(argsObj, ct);
            return (r?.ToString() ?? "", null, null);
        }
        catch (Exception ex)
        {
            return (null, $"Error executing '{tool.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>Track memory clean blocks for removal from messageList after LLM consumes them.</summary>
    private void TrackMemoryClean(IDictionary<string, object?>? args, string toolName, string? resultText, string? errorText)
    {
        if (errorText is null && toolName == "memory" && resultText?.StartsWith("Deleted ") == true)
        {
            try
            {
                var argsJson = args is not null ? JsonSerializer.Serialize(args) : "{}";
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("blocks", out var blk) && blk.ValueKind == JsonValueKind.Array)
                {
                    foreach (var num in blk.EnumerateArray().Select(e => e.GetDouble()))
                        _pendingMemoryCleanBlocks.Add(num);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FailSafeChatClient] Failed to track memory clean blocks: {ex.Message}");
            }
        }
    }

    private async Task<List<ChatMessage>> ExecuteTools(
        IReadOnlyList<FunctionCallContent> fccs, ChatOptions? options, CancellationToken ct)
    {
        var tools = options?.Tools?.OfType<AIFunction>().ToList() ?? [];
        var results = new List<ChatMessage>();
        var hasError = false;

        for (var i = 0; i < fccs.Count; i++)
        {
            var fcc = fccs[i];
            var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");

            if (hasError)
            {
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, $"Skipped — previous tool errored"), new TextContent("Skipped — previous tool errored")]));
                continue;
            }

            var tool = tools.FirstOrDefault(t => t.Name == fcc.Name);
            if (tool is null)
            {
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, $"No tool found: '{fcc.Name}'"), new TextContent($"No tool found: '{fcc.Name}'")]));
                continue;
            }

            try
            {
                var args = fcc.Arguments is not null ? new AIFunctionArguments(fcc.Arguments) : null;
                var result = await tool.InvokeAsync(args, ct);
                var resultText = result?.ToString() ?? "";
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result), new TextContent(resultText)]));
                ExecutedCallIds.Add(callId);
            }
            catch (Exception ex)
            {
                var skipped = fccs.Count - i - 1;
                var msg = $"Error executing '{fcc.Name}': {ex.Message}";
                if (skipped > 0) msg += $"; {skipped} tool call(s) were not executed";
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, msg) { Exception = ex }, new TextContent(msg)]));
                ExecutedCallIds.Add(callId);
                hasError = true;
            }
        }

        return results;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var messageList = messages.ToList();

        for (var iteration = 0; iteration < _maxIterations; iteration++)
        {
            var response = await base.GetResponseAsync(messageList, options, ct);
            var assistantMsg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (assistantMsg is null) return response;

            var fccs = assistantMsg.Contents.OfType<FunctionCallContent>().ToList();
            if (fccs.Count == 0) return response;

            messageList.Add(assistantMsg);
            messageList.AddRange(await ExecuteTools(fccs, options, ct));
        }

        throw new InvalidOperationException($"Tool execution exceeded {_maxIterations} iterations.");
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();

        for (var iteration = 0; iteration < _maxIterations; iteration++)
        {
            var allUpdates = new List<ChatResponseUpdate>();
            var hasToolCall = false;

            await foreach (var update in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
            {
                allUpdates.Add(update);

                if (update.Contents.OfType<FunctionCallContent>().Any(fc => fc.Name is not null))
                    hasToolCall = true;

                if (update.Contents.Any(c => c is TextContent or TextReasoningContent))
                    yield return update;
            }

            // Peek cleanup: truncate tool results that LLM just consumed (seen exactly once)
            // Must keep FunctionResultContent (same CallId) or API rejects unmatched tool_calls
            if (_pendingPeekCallIds.Count > 0)
            {
                foreach (var m in messageList)
                {
                    if (m.Role != ChatRole.Tool) continue;
                    var shouldTruncate = m.Contents.OfType<FunctionResultContent>().Any(frc =>
                        _pendingPeekCallIds.Contains(frc.CallId));
                    if (!shouldTruncate) continue;
                    foreach (var frc in m.Contents.OfType<FunctionResultContent>())
                        frc.Result = "(peek)";
                    foreach (var tc in m.Contents.OfType<TextContent>())
                        tc.Text = "(peek)";
                }
                _pendingPeekCallIds.Clear();
            }

            // Memory clean: remove deleted blocks from messageList after LLM consumed the result
            if (_pendingMemoryCleanBlocks.Count > 0)
            {
                ToolCallHelper.RemoveBlocksFromMessageList(messageList, _pendingMemoryCleanBlocks);
                _pendingMemoryCleanBlocks.Clear();
            }

            if (!hasToolCall)
            {
                var chatResponse = allUpdates.ToChatResponse();
                var (cacheHit, cacheMiss, cacheOutput) = ExtractCacheTokens(allUpdates);
                if (cacheHit > 0 || cacheMiss > 0 || cacheOutput > 0)
                {
                    TotalCacheHitTokens += cacheHit;
                    TotalCacheMissTokens += cacheMiss;
                    TotalOutputTokens += cacheOutput;
                    LastHitTokens = cacheHit;
                    LastMissTokens = cacheMiss;
                    OnUsage?.Invoke(cacheHit, cacheMiss, cacheOutput);
                }
                var finalAssistant = chatResponse.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
                if (finalAssistant is not null)
                {
                    var finalContents = new List<AIContent>();
                    var allReasoning = string.Concat(allUpdates
                        .SelectMany(u => u.Contents)
                        .OfType<TextReasoningContent>()
                        .Select(r => r.Text));
                    var allText = string.Concat(allUpdates
                        .SelectMany(u => u.Contents)
                        .OfType<TextContent>()
                        .Select(t => t.Text));
                    if (!string.IsNullOrEmpty(allReasoning))
                        finalContents.Add(new TextReasoningContent(allReasoning));
                    if (!string.IsNullOrEmpty(allText))
                        finalContents.Add(new TextContent(allText));
                    if (finalContents.Count > 0)
                        messageList.Add(new ChatMessage(ChatRole.Assistant, finalContents));
                }
                LastMessages = messageList.AsReadOnly();
                yield break;
            }

            var toolResponse = allUpdates.ToChatResponse();
            var (tHit, tMiss, tOutput) = ExtractCacheTokens(allUpdates);
            if (tHit > 0 || tMiss > 0 || tOutput > 0)
            {
                TotalCacheHitTokens += tHit;
                TotalCacheMissTokens += tMiss;
                TotalOutputTokens += tOutput;
                OnUsage?.Invoke(tHit, tMiss, tOutput);
            }
            var toolAssistant = toolResponse.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (toolAssistant is null) yield break;

            var fccs = toolAssistant.Contents.OfType<FunctionCallContent>().ToList();
            if (fccs.Count == 0) yield break;

            // ensure every tool call has an explicit CallId (streaming may omit it)
            var fixedFccs = fccs.Select(fcc =>
            {
                if (string.IsNullOrEmpty(fcc.CallId))
                {
                    var newId = Guid.NewGuid().ToString("N");
                    return new FunctionCallContent(newId, fcc.Name ?? "", fcc.Arguments ?? new Dictionary<string, object?>());
                }
                return fcc;
            }).ToList();

            // rebuild assistant message with fixed CallIds
            var nonFccContent = allUpdates
                .SelectMany(u => u.Contents)
                .Where(c => c is TextContent or TextReasoningContent)
                .ToList();
            var mergedContent = new List<AIContent>();
            var reasoningText = string.Concat(nonFccContent.OfType<TextReasoningContent>().Select(r => r.Text));
            var responseText = string.Concat(nonFccContent.OfType<TextContent>().Select(t => t.Text));
            if (!string.IsNullOrEmpty(reasoningText))
                mergedContent.Add(new TextReasoningContent(reasoningText));
            if (!string.IsNullOrEmpty(responseText))
                mergedContent.Add(new TextContent(responseText));
            mergedContent.AddRange(fixedFccs);
            var fixedAssistant = new ChatMessage(ChatRole.Assistant, mergedContent);

            messageList.Add(fixedAssistant);

            // ── Tool grouping: consecutive parallel-safe tools execute concurrently ──
            var tools = options?.Tools?.OfType<AIFunction>().ToList() ?? [];
            var toolResults = new List<ChatMessage>();
            var hasError = false;

            var groups = BuildToolGroups(fixedFccs);

            foreach (var group in groups)
            {
                if (hasError)
                {
                    foreach (var fcc in group)
                    {
                        var skipId = fcc.CallId ?? Guid.NewGuid().ToString("N");
                        var skipped = "Skipped — previous tool errored";
                        toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(skipId, skipped), new TextContent(skipped)]));
                        yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(skipId, skipped)] };
                    }
                    continue;
                }

                // ── Sequential: yield FCC immediately, then execute ──
                if (group.Count == 1)
                {
                    var fcc = group[0];
                    var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");
                    var toolName = fcc.Name ?? "";
                    var isPeek = ToolCallHelper.IsPeekCall(toolName, fcc.Arguments);

                    // Yield FCC before execution — user sees the tool call header immediately
                    yield return new ChatResponseUpdate
                    {
                        Contents = [new FunctionCallContent(callId, toolName, fcc.Arguments ?? new Dictionary<string, object?>())]
                    };

                    // Execute tool
                    var tool = tools.FirstOrDefault(t => t.Name == toolName);
                    string? resultText = null, errorText = null;
                    Exception? exception = null;

                    if (tool is null)
                    {
                        errorText = $"No tool found: '{toolName}'";
                    }
                    else
                    {
                        (resultText, errorText, exception) = await RunToolAsync(tool, fcc.Arguments, cancellationToken);
                    }

                    if (isPeek) _pendingPeekCallIds.Add(callId);

                    if (errorText is not null)
                    {
                        toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, errorText) { Exception = exception }, new TextContent(errorText)]));
                        yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, errorText)] };
                        hasError = true;
                    }
                    else
                    {
                        toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, resultText ?? ""), new TextContent(resultText ?? "")]));
                        yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, resultText ?? "")] };
                    }

                    ExecutedCallIds.Add(callId);
                    TrackMemoryClean(fcc.Arguments, toolName, resultText, errorText);
                }
                // ── Parallel: start all tasks concurrently, yield FCC+FRC as each completes ──
                else
                {
                    var execData = new List<(string CallId, string ToolName, bool IsPeek, Task<(string? Result, string? Error, Exception? Exception)> Task)>();

                    foreach (var fcc in group)
                    {
                        var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");
                        var toolName = fcc.Name ?? "";

                        var isPeek = ToolCallHelper.IsPeekCall(toolName, fcc.Arguments);

                        var tool = tools.FirstOrDefault(t => t.Name == toolName);
                        if (tool is null)
                        {
                            execData.Add((callId, toolName, isPeek,
                                Task.FromResult<(string?, string?, Exception?)>((null, $"No tool found: '{toolName}'", null))));
                            continue;
                        }

                        Task<(string?, string?, Exception?)> t = RunToolAsync(tool, fcc.Arguments, cancellationToken);
                        execData.Add((callId, toolName, isPeek, t));
                    }

                    // Yield FCC + FRC as each task completes
                    var pendingIds = execData.Select((d, idx) => (task: d.Task, idx)).ToList();
                    while (pendingIds.Count > 0)
                    {
                        var done = await Task.WhenAny(pendingIds.Select(p => p.task));
                        var item = pendingIds.First(p => p.task == done);
                        pendingIds.Remove(item);

                        var (callId, toolName, isPeek, _) = execData[item.idx];
                        var (resultText, errorText, exception) = await done;
                        var fcc = group[item.idx];

                        if (isPeek) _pendingPeekCallIds.Add(callId);

                        yield return new ChatResponseUpdate
                        {
                            Contents = [new FunctionCallContent(callId, toolName, fcc.Arguments ?? new Dictionary<string, object?>())]
                        };

                        if (errorText is not null)
                        {
                            toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, errorText) { Exception = exception }, new TextContent(errorText)]));
                            yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, errorText)] };
                            hasError = true;
                        }
                        else
                        {
                            toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, resultText ?? ""), new TextContent(resultText ?? "")]));
                            yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, resultText ?? "")] };
                        }

                        ExecutedCallIds.Add(callId);
                        TrackMemoryClean(fcc.Arguments, toolName, resultText, errorText);
                    }
                }
            }

            messageList.AddRange(toolResults);

            // Flush queued parallel subagent tasks; append results to tool messages for LLM
            if (OnBatchComplete is not null)
            {
                var flushText = await OnBatchComplete();
                if (!string.IsNullOrEmpty(flushText))
                {
                    var flushId = Guid.NewGuid().ToString("N");
                    messageList.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(flushId, flushText), new TextContent(flushText)]));
                }
            }
        }

        throw new InvalidOperationException($"Tool execution exceeded {_maxIterations} iterations.");
    }

    private static (long Hit, long Miss, long Output) ExtractCacheTokens(List<ChatResponseUpdate> updates)
    {
        var hit = 0L;
        var miss = 0L;
        var output = 0L;

        foreach (var update in updates)
        {
            if (update.RawRepresentation is null) continue;

            try
            {
                using var doc = GetUsageDocument(update.RawRepresentation);
                if (doc is null) continue;
                if (!doc.RootElement.TryGetProperty("Usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                    continue;

                var inputTotal = usage.TryGetProperty("InputTokenCount", out var itc) && itc.ValueKind == JsonValueKind.Number
                    ? itc.GetInt64() : 0L;
                var cached = 0L;
                if (usage.TryGetProperty("InputTokenDetails", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    if (details.TryGetProperty("CachedTokenCount", out var ctc) && ctc.ValueKind == JsonValueKind.Number)
                        cached = ctc.GetInt64();
                }

                if (cached > 0)
                {
                    hit = Math.Max(hit, cached);
                    miss = Math.Max(miss, inputTotal - cached);
                }
                else if (inputTotal > 0)
                {
                    miss = Math.Max(miss, inputTotal);
                }

                if (usage.TryGetProperty("OutputTokenCount", out var otc) && otc.ValueKind == JsonValueKind.Number)
                    output = Math.Max(output, otc.GetInt64());
            }
            catch
            {
            }
        }

        return (hit, miss, output);
    }

    /// <summary>Parse RawRepresentation into a JsonDocument without double-serialization.
    /// RawRepresentation is typically already a JsonDocument or JsonElement from the OpenAI client.</summary>
    private static JsonDocument? GetUsageDocument(object raw)
    {
        return raw switch
        {
            JsonDocument jd => JsonDocument.Parse(jd.RootElement.GetRawText()),
            JsonElement je => JsonDocument.Parse(je.GetRawText()),
            _ => JsonDocument.Parse(JsonSerializer.Serialize(raw))
        };
    }
}