using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Glyphite.Host.Services;

public class TurnProcessor : ITurnProcessor
{
    private readonly IMemoryStore _store;
    private readonly IBlockMemoryProvider _blockMemory;
    private readonly IChatClient _chatClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly IConfigService _cfgService;
    private readonly SubAgentManager _subAgentManager;
    private readonly DeepSeekOptions _deepseek;
    private readonly AgentOptions _agentOpts;
    private readonly ToolStreamingOptions _streamOpts;
    public TurnProcessor(
        IMemoryStore store,
        IBlockMemoryProvider blockMemory,
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        IConfigService cfgService,
        SubAgentManager subAgentManager,
        IOptions<DeepSeekOptions> deepseek,
        IOptions<AgentOptions> agentOpts,
        IOptions<ToolStreamingOptions> streamOpts)
    {
        _store = store;
        _blockMemory = blockMemory;
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _cfgService = cfgService;
        _subAgentManager = subAgentManager;
        _deepseek = deepseek.Value;
        _agentOpts = agentOpts.Value;
        _streamOpts = streamOpts.Value;
    }

    public async IAsyncEnumerable<TurnEvent> ProcessAsync(
        string sessionId,
        string input,
        ChatOptions chatOptions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var modelStr = chatOptions.ModelId ?? _deepseek.Model;

        var includeMemory = chatOptions.AdditionalProperties?.ContainsKey("saveMemory") == true;
        chatOptions.Tools = _toolRegistry.GetBuiltinTools(sessionId, includeMemory).ToList();

        // Get peek block stats before cleaning (for informative message)
        var peekStats = await _store.GetPeekBlockStatsAsync(sessionId);
        var peekCleaned = await _store.RemovePeekBlocksAsync(sessionId);

        var contextMessages = await _blockMemory.BuildContextAsync(
            sessionId, modelStr, _deepseek.ContextWindow);

        var nextNum = await _store.GetNextNumberAsync(sessionId);
        if (nextNum <= 0) nextNum = 1;

        // Auto-tool: peek cleanup — visible to user AND model
        if (peekCleaned > 0)
        {
            var peekMsg = BuildPeekCleanMessage(peekCleaned, peekStats);
            var cleanArgs = $"{{\"count\":{peekCleaned}}}";
            yield return new AutoToolTurnEvent("peek_clean", cleanArgs, false, peekMsg);

            var autoBlock = MemoryBlock.AutoTool("peek_clean", cleanArgs, peekMsg, modelStr);
            autoBlock.Number = nextNum++;
            await _store.AppendBlocksAsync(sessionId, [autoBlock], nextNum);

            contextMessages.Add(new ChatMessage(ChatRole.System, autoBlock.ToContextString()));
        }

        var initialMessages = new List<ChatMessage>();
        initialMessages.AddRange(contextMessages);
        initialMessages.Add(new ChatMessage(ChatRole.User, input));

        // Wrap with session-aware client so DeepSeek gets `user` = sessionId for cache isolation
        var sessionClient = new SessionChatClient(_chatClient, sessionId);
        var failSafeClient = new FailSafeChatClient(
            sessionClient, _agentOpts.MaxToolIterations, _streamOpts);

        // Flush queued parallel subagent tasks after each tool batch
        failSafeClient.OnBatchComplete = async () =>
        {
            var flushResults = await _subAgentManager.FlushParallelAsync();
            if (flushResults.Count == 0) return null;

            var lines = new List<string> { $"[subagent_flush] {flushResults.Count} parallel task(s) completed:" };
            foreach (var (agentId, result, error) in flushResults)
            {
                if (error is not null)
                    lines.Add($"  [{agentId}] Error: {error}");
                else
                    lines.Add($"  [{agentId}]\n    {(result ?? "(no output)").Replace("\n", "\n    ")}");
            }
            return string.Join("\n", lines);
        };

        _blockMemory.CurrentExecutedIds.Value = failSafeClient.ExecutedCallIds;

        var userBlock = MemoryBlock.UserMessage(input);
        userBlock.Number = nextNum++;
        await _store.AppendBlocksAsync(sessionId, [userBlock], nextNum);

        // ── Per-turn state (local variables, not instance fields) ──
        var reasoningAccum = new StringBuilder();
        var textAccum = new StringBuilder();
        var pendingToolCalls = new List<(string name, string args, bool isPeek, double blockNumber)>();

        async Task<List<TurnEvent>> ProcessUpdate(ChatResponseUpdate update)
        {
            var events = new List<TurnEvent>();

            var text = string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
            var reasoning = string.Concat(update.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
            var fcc = update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
            var frc = update.Contents.OfType<FunctionResultContent>().FirstOrDefault();

            if (!string.IsNullOrEmpty(reasoning))
            {
                reasoningAccum.Append(reasoning);
                events.Add(new ReasoningChunkEvent(reasoning));
            }

            if (!string.IsNullOrEmpty(text))
            {
                textAccum.Append(text);
                events.Add(new TextChunkEvent(text));
            }

            if (fcc is not null)
            {
                events.AddRange(await FlushReasoning(_agentOpts.PeekToolReasoning));
                events.AddRange(await FlushText());

                var args = JsonSerializer.Serialize(fcc.Arguments ?? new Dictionary<string, object?>(),
                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

                var isPeek = fcc.Arguments?.TryGetValue("peek", out var peekVal) == true &&
                    (peekVal is bool pb ? pb : (peekVal is JsonElement je && je.ValueKind == JsonValueKind.True));

                var callBlock = MemoryBlock.ToolCall(fcc.Name, args, model: modelStr);
                if (isPeek)
                    callBlock.Data = new() { ["peek"] = true };
                callBlock.Number = nextNum++;
                await _store.AppendBlocksAsync(sessionId, [callBlock], nextNum);

                pendingToolCalls.Add((fcc.Name, args, isPeek, callBlock.Number));

                events.Add(new ToolCallTurnEvent(fcc.Name, args, isPeek));
            }

            if (frc is not null)
            {
                events.AddRange(await FlushReasoning(_agentOpts.PeekToolReasoning));
                events.AddRange(await FlushText());

                var output = frc.Result?.ToString() ?? "";
                if (pendingToolCalls.Count > 0)
                {
                    var (name, args, isPeek, callBlockNumber) = pendingToolCalls[0];
                    pendingToolCalls.RemoveAt(0);

                    if (name is "read_file" or "write_file")
                    {
                        string? fPath = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(args);
                            if (doc.RootElement.TryGetProperty("path", out var p))
                                fPath = p.GetString();
                        }
                        catch { }

                        if (fPath is not null)
                        {
                            string fileContent;
                            if (name == "write_file")
                            {
                                try { fileContent = await File.ReadAllTextAsync(fPath); }
                                catch { fileContent = output; }
                            }
                            else
                            {
                                fileContent = output.TrimEnd('\n', '\r');
                            }

                            if (!isPeek && !string.IsNullOrEmpty(fileContent))
                                await _store.UpdateBlockToolResultAsync(sessionId, callBlockNumber, fileContent);

                            var cleanedArgs = CleanToolArgs(args, "content");
                            if (cleanedArgs is not null)
                                await _store.UpdateBlockAsync(sessionId, callBlockNumber, content: cleanedArgs);

                            events.Add(new ToolResultTurnEvent(name, fileContent));
                        }
                        else
                        {
                            events.Add(new ToolResultTurnEvent(name, output));
                        }
                    }
                    else if (name == "patch_file")
                    {
                        if (!isPeek && !string.IsNullOrEmpty(output))
                            await _store.UpdateBlockToolResultAsync(sessionId, callBlockNumber, output);

                        var cleanedArgs = CleanToolArgs(args, "newString", "oldString");
                        if (cleanedArgs is not null)
                            await _store.UpdateBlockAsync(sessionId, callBlockNumber, content: cleanedArgs);

                        events.Add(new ToolResultTurnEvent(name, output));
                    }
                    else
                    {
                        if (!isPeek && !string.IsNullOrEmpty(output))
                            await _store.UpdateBlockToolResultAsync(sessionId, callBlockNumber, output);

                        events.Add(new ToolResultTurnEvent(name, output));
                    }

                    // ── After tool execution: update in-turn context ──
                    // Memory clean: remove matching ChatMessages from context (by block numbers in args)
                    if (name == "memory" && output.StartsWith("Deleted "))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(args);
                            if (doc.RootElement.TryGetProperty("blocks", out var blk) && blk.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var num in blk.EnumerateArray().Select(e => e.GetDouble()))
                                {
                                    var pat = $"[Block: {num:F1},";
                                    for (var i = contextMessages.Count - 1; i >= 0; i--)
                                    {
                                        if (contextMessages[i].Text?.StartsWith(pat) == true)
                                        {
                                            contextMessages.RemoveAt(i);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    // Peek tools: replace ChatMessage with cleaned block render (ToContextString)
                    else if (isPeek)
                    {
                        var updatedBlock = await _store.GetBlockAsync(sessionId, callBlockNumber);
                        if (updatedBlock is not null)
                        {
                            var newText = updatedBlock.ToContextString();
                            var pat = $"[Block: {callBlockNumber:F1},";
                            for (var i = 0; i < contextMessages.Count; i++)
                            {
                                if (contextMessages[i].Text?.StartsWith(pat) == true)
                                {
                                    contextMessages[i] = new ChatMessage(ChatRole.System, newText);
                                    break;
                                }
                            }
                        }
                    }

                    // Inter-iteration: clear peek markers (not reasoning)
                    if (pendingToolCalls.Count == 0)
                        await _store.ClearPeekMarkersAsync(sessionId, false);
                }
                else
                {
                    var block = MemoryBlock.ToolCall("unknown", output, model: modelStr);
                    block.Number = nextNum++;
                    block.ToolResult = output;
                    await _store.AppendBlocksAsync(sessionId, [block], nextNum);
                }
            }

            return events;
        }

        async Task<List<TurnEvent>> FlushReasoning(bool isPeek)
        {
            if (reasoningAccum.Length == 0) return [];
            var fullReasoning = reasoningAccum.ToString();
            reasoningAccum.Clear();
            var block = MemoryBlock.AgentReasoning(fullReasoning, model: modelStr);
            block.Number = nextNum++;
            if (isPeek)
                block.Data = new() { ["peek"] = true };
            await _store.AppendBlocksAsync(sessionId, [block], nextNum);
            return [];
        }

        async Task<List<TurnEvent>> FlushText()
        {
            if (textAccum.Length == 0) return [];
            var fullText = textAccum.ToString();
            textAccum.Clear();
            var block = MemoryBlock.AgentMessage(fullText, model: modelStr);
            block.Number = nextNum++;
            await _store.AppendBlocksAsync(sessionId, [block], nextNum);
            return [];
        }

        async Task<List<TurnEvent>> FlushAll()
        {
            var events = new List<TurnEvent>();
            events.AddRange(await FlushReasoning(_agentOpts.PeekReasoning));
            events.AddRange(await FlushText());
            return events;
        }

        await foreach (var update in failSafeClient
            .GetStreamingResponseAsync(initialMessages, chatOptions)
            .WithCancellation(ct))
        {
            var events = await ProcessUpdate(update);
            foreach (var e in events)
                yield return e;
        }

        // Persist usage from all iterations
        await _store.RecordUsageAsync(sessionId, failSafeClient.TotalCacheHitTokens, failSafeClient.TotalCacheMissTokens, failSafeClient.TotalOutputTokens, failSafeClient.LastHitTokens, failSafeClient.LastMissTokens, model: modelStr);
        yield return new UsageTurnEvent(failSafeClient.TotalCacheHitTokens, failSafeClient.TotalCacheMissTokens, failSafeClient.TotalOutputTokens, failSafeClient.LastHitTokens, failSafeClient.LastMissTokens);

        // Final flush: any remaining parallel tasks that were never flushed by a sequential call
        if (_subAgentManager.HasPendingParallel)
        {
            var finalResults = await _subAgentManager.FlushParallelAsync();
            foreach (var (agentId, result, error) in finalResults)
            {
                var msg = error is not null
                    ? $"[AutoTool: subagent_flush] [{agentId}] Error: {error}"
                    : $"[AutoTool: subagent_flush] [{agentId}]\n{result}";
                yield return new TextTurnEvent(msg);
            }
        }

        var flushEvents = await FlushAll();
        foreach (var e in flushEvents)
            yield return e;
    }

    private static string BuildPeekCleanMessage(int total, Dictionary<string, int> stats)
    {
        var iconMap = new Dictionary<string, string>
        {
            ["user_message"] = "👤", ["agent_message"] = "💬", ["agent_reasoning"] = "🧠",
            ["tool"] = "🔧", ["todo"] = "📋", ["todo_update"] = "🔄",
            ["auto_tool"] = "🤖"
        };
        var lines = new List<string> { $"── Cleaned {total} peek blocks ─────────────────" };
        foreach (var kv in stats.OrderByDescending(kv => kv.Value))
        {
            var icon = iconMap.GetValueOrDefault(kv.Key, "  ");
            lines.Add($"  {icon} {kv.Key,-20}: {kv.Value,4}");
        }
        return string.Join('\n', lines);
    }

    private static string? CleanToolArgs(string argsJson, params string[] keys)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var dict = new Dictionary<string, JsonElement?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value;

            foreach (var key in keys)
                dict.Remove(key);

            var cleaned = JsonSerializer.Serialize(
                dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            return cleaned == argsJson ? null : cleaned;
        }
        catch
        {
            return null;
        }
    }
}
