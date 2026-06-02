using System.Runtime.CompilerServices;
using System.Text.Json;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

public sealed class FailSafeChatClient : DelegatingChatClient
{
    private readonly int _maxIterations;
    private readonly ToolStreamingOptions _streamOpts;

    public HashSet<string> ExecutedCallIds { get; } = [];
    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
    public Action<long, long, long>? OnUsage { get; set; }

    public long TotalCacheHitTokens { get; private set; }
    public long TotalCacheMissTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }

    public FailSafeChatClient(IChatClient inner, int maxIterations, ToolStreamingOptions streamOpts) : base(inner)
    {
        _maxIterations = maxIterations;
        _streamOpts = streamOpts;
    }

    private int GetMaxLength(string toolName)
        => _streamOpts.ToolMaxLength.TryGetValue(toolName, out var len) ? len : -1;

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
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, $"Skipped вЂ” previous tool errored"), new TextContent("Skipped вЂ” previous tool errored")]));
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

            if (!hasToolCall)
            {
                var chatResponse = allUpdates.ToChatResponse();
                var (cacheHit, cacheMiss, cacheOutput) = ExtractCacheTokens(allUpdates);
                if (cacheHit > 0 || cacheMiss > 0 || cacheOutput > 0)
                {
                    TotalCacheHitTokens += cacheHit;
                    TotalCacheMissTokens += cacheMiss;
                    TotalOutputTokens += cacheOutput;
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

            // Execute tools one by one, yielding fcc before each and frc after
            var tools = options?.Tools?.OfType<AIFunction>().ToList() ?? [];
            var toolResults = new List<ChatMessage>();
            var hasError = false;

            for (var i = 0; i < fixedFccs.Count; i++)
            {
                var fcc = fixedFccs[i];
                var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");
                var toolName = fcc.Name ?? "";

                // Yield tool call before execution — user sees "[Tool: name | args]" immediately
                yield return new ChatResponseUpdate
                {
                    Contents = [new FunctionCallContent(callId, toolName, fcc.Arguments ?? new Dictionary<string, object?>())]
                };

                if (hasError)
                {
                    var skipped = $"Skipped — previous tool errored";
                    toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, skipped), new TextContent(skipped)]));
                    yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, skipped)] };
                    continue;
                }

                var tool = tools.FirstOrDefault(t => t.Name == toolName);
                if (tool is null)
                {
                    var errMsg = $"No tool found: '{toolName}'";
                    toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, errMsg), new TextContent(errMsg)]));
                    yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, errMsg)] };
                    continue;
                }

                // Execute — capture result/error outside try-catch (yield forbidden inside try-catch)
                string? resultText = null;
                string? errorText = null;
                Exception? exception = null;

                try
                {
                    var argsObj = fcc.Arguments is not null ? new AIFunctionArguments(fcc.Arguments) : null;
                    var r = await tool.InvokeAsync(argsObj, cancellationToken);
                    resultText = r?.ToString() ?? "";
                }
                catch (Exception ex)
                {
                    errorText = $"Error executing '{toolName}': {ex.Message}";
                    var skipped = fixedFccs.Count - i - 1;
                    if (skipped > 0) errorText += $"; {skipped} tool call(s) were not executed";
                    exception = ex;
                    hasError = true;
                }

                if (errorText is not null)
                {
                    toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, errorText) { Exception = exception }, new TextContent(errorText)]));
                    yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, errorText)] };
                }
                else
                {
                    toolResults.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, resultText), new TextContent(resultText ?? "")]));
                    var maxLen = GetMaxLength(toolName);
                    var display = maxLen > 0 && (resultText?.Length ?? 0) > maxLen
                        ? resultText![..maxLen] + "..."
                        : maxLen == 0 ? "" : resultText ?? "";
                    yield return new ChatResponseUpdate { Contents = [new FunctionResultContent(callId, display)] };
                }

                ExecutedCallIds.Add(callId);
            }

            messageList.AddRange(toolResults);
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
                var json = JsonSerializer.Serialize(update.RawRepresentation);
                using var doc = JsonDocument.Parse(json);
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
}
