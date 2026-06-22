using System.Runtime.CompilerServices;
using System.Text.Json;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Orchestrates the LLM request loop: sends messages, collects streaming updates,
/// delegates tool execution to <see cref="ToolExecutor"/>, tracks usage via <see cref="UsageTracker"/>.
/// On each iteration, if the LLM requests tools, they are grouped and executed,
/// then the results are fed back for the next iteration.
/// </summary>
public sealed class FailSafeChatClient : DelegatingChatClient
{
    private readonly int _maxIterations;
    private readonly ILogger _logger;
    private readonly ToolExecutor _toolExecutor;
    private readonly UsageTracker _usageTracker;

    public HashSet<string> ExecutedCallIds => _toolExecutor.ExecutedCallIds;

    /// <summary>Expose the usage tracker so TurnProcessor can read final values.</summary>
    public UsageTracker Usage => _usageTracker;

    public long TotalCacheHitTokens => _usageTracker.TotalCacheHitTokens;
    public long TotalCacheMissTokens => _usageTracker.TotalCacheMissTokens;
    public long TotalOutputTokens => _usageTracker.TotalOutputTokens;
    public long LastHitTokens => _usageTracker.LastHitTokens;
    public long LastMissTokens => _usageTracker.LastMissTokens;

    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
    public Action<long, long, long>? OnUsage { get; set; }

    /// <summary>Called after each batch of tool executions completes. Returns text to append to tool results (or null).</summary>
    public Func<Task<string?>>? OnBatchComplete { get; set; }

    public FailSafeChatClient(IChatClient inner, int maxIterations, ILogger logger) : base(inner)
    {
        _maxIterations = maxIterations;
        _logger = logger;
        _toolExecutor = new ToolExecutor(logger);
        _usageTracker = new UsageTracker();
        _usageTracker.OnUsage += (hit, miss, output) => OnUsage?.Invoke(hit, miss, output);
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
            messageList.AddRange(await _toolExecutor.ExecuteTools(fccs, options, ct));
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
            _toolExecutor.CleanupPeekTools(messageList);

            // Memory clean: remove deleted blocks from messageList after LLM consumed the result
            _toolExecutor.CleanupMemoryClean(messageList);

            if (!hasToolCall)
            {
                var chatResponse = allUpdates.ToChatResponse();
                _usageTracker.RecordUsage(allUpdates);

                _logger.LogInformation("Completed in {Iteration} iteration(s): hit={Hit} miss={Miss} out={Output}",
                    iteration + 1, _usageTracker.TotalCacheHitTokens, _usageTracker.TotalCacheMissTokens, _usageTracker.TotalOutputTokens);
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

            // Record usage for tool-calling iteration
            _usageTracker.RecordUsage(allUpdates);

            var toolResponse = allUpdates.ToChatResponse();
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

            var groups = ToolExecutor.BuildToolGroups(fixedFccs);

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
                        (resultText, errorText, exception) = await ToolExecutor.RunToolAsync(tool, fcc.Arguments, cancellationToken);
                    }

                    if (isPeek) _toolExecutor.PendingPeekCallIds.Add(callId);

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

                    _toolExecutor.ExecutedCallIds.Add(callId);
                    _toolExecutor.TrackMemoryClean(fcc.Arguments, toolName, resultText, errorText);
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

                        Task<(string?, string?, Exception?)> t = ToolExecutor.RunToolAsync(tool, fcc.Arguments, cancellationToken);
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

                        if (isPeek) _toolExecutor.PendingPeekCallIds.Add(callId);

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

                        _toolExecutor.ExecutedCallIds.Add(callId);
                        _toolExecutor.TrackMemoryClean(fcc.Arguments, toolName, resultText, errorText);
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
}
