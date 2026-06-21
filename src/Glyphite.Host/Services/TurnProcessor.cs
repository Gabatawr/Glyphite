using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Glyphite.Host.Utils;
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
    private readonly ISubAgentConfigLoader _configLoader;

    private static readonly Dictionary<string, string[]> FileToolCleanArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = ["content"],
        ["write_file"] = ["content"],
        ["patch_file"] = ["newString", "oldString"]
    };

    public TurnProcessor(
        IMemoryStore store,
        IBlockMemoryProvider blockMemory,
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        IConfigService cfgService,
        SubAgentManager subAgentManager,
        ISubAgentConfigLoader configLoader,
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
        _configLoader = configLoader;
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
        // Reload config from disk every turn so changes to Glyphite.json
        // and Glyphite.{sessionId}.json are picked up without restart.
        var parentCwd = Directory.GetCurrentDirectory();
        var agentCwd = await _store.GetAgentHomePathAsync(sessionId) ?? parentCwd;
        await _configLoader.LoadConfigAsync(sessionId, agentCwd, parentCwd);

        // Fresh options this turn — IOptions<T> DI values may be stale within agent scope.
        // Tools get their own fresh config via _cfgService.GetOptionsAsync<T>() internally.
        var deepseekOpts = await _cfgService.GetOptionsAsync<DeepSeekOptions>("DeepSeek", sessionId);
        var agentOpts = await _cfgService.GetOptionsAsync<AgentOptions>("Agent", sessionId);

        var modelStr = chatOptions.ModelId ?? deepseekOpts.Model;

        var includeMemory = chatOptions.AdditionalProperties?.ContainsKey("saveMemory") == true;
        chatOptions.Tools = (await _toolRegistry.GetBuiltinToolsAsync(sessionId, includeMemory)).ToList();

        // Get peek block stats before cleaning (for informative message)
        var peekStats = await _store.GetPeekBlockStatsAsync(sessionId);
        var peekCleaned = await _store.RemovePeekBlocksAsync(sessionId);

        var contextMessages = await _blockMemory.BuildContextAsync(
            sessionId, modelStr, deepseekOpts.ContextWindow);

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
            sessionClient, agentOpts.MaxToolIterations);

        // No OnBatchComplete needed — subagent tasks run synchronously within their group's Task.WhenAll

        _blockMemory.CurrentExecutedIds.Value = failSafeClient.ExecutedCallIds;

        var userBlock = MemoryBlock.UserMessage(input);
        userBlock.Number = nextNum++;
        await _store.AppendBlocksAsync(sessionId, [userBlock], nextNum);

        // ── Per-turn state (local variables, not instance fields) ──
        var reasoningAccum = new StringBuilder();
        var textAccum = new StringBuilder();
        var pendingToolCalls = new Dictionary<string, (string name, string args, bool isPeek, double blockNumber)>();

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
                events.AddRange(await FlushReasoning(agentOpts.PeekToolReasoning));
                events.AddRange(await FlushText());

                var args = JsonSerializer.Serialize(fcc.Arguments ?? new Dictionary<string, object?>(),
                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

                var isPeek = ToolCallHelper.IsPeekCall(fcc);

                var callBlock = MemoryBlock.ToolCall(fcc.Name, args, model: modelStr);
                if (isPeek)
                    callBlock.Data = new() { ["peek"] = true };
                callBlock.Number = nextNum++;
                await _store.AppendBlocksAsync(sessionId, [callBlock], nextNum);

                var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");
                pendingToolCalls[callId] = (fcc.Name, args, isPeek, callBlock.Number);

                events.Add(new ToolCallTurnEvent(fcc.Name, args, isPeek));
            }

            if (frc is not null)
            {
                events.AddRange(await FlushReasoning(agentOpts.PeekToolReasoning));
                events.AddRange(await FlushText());

                var output = frc.Result?.ToString() ?? "";
                var frcCallId = frc.CallId;
                if (frcCallId is not null && pendingToolCalls.TryGetValue(frcCallId, out var pending))
                {
                    pendingToolCalls.Remove(frcCallId);
                    var (name, args, isPeek, callBlockNumber) = pending;

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
                            await EmitToolResult(fileContent, name == "write_file" ? ["content"] : null);
                        }
                        else
                        {
                            await EmitToolResult(output, FileToolCleanArgs.GetValueOrDefault(name));
                        }
                    }
                    else
                    {
                        FileToolCleanArgs.TryGetValue(name, out var cleanKeys);
                        await EmitToolResult(output, cleanKeys);
                    }

                    // ── After tool execution: update in-turn context ──

                    async Task EmitToolResult(string emitOutput, string[]? cleanKeys)
                    {
                        if (!isPeek && !string.IsNullOrEmpty(emitOutput))
                            await _store.UpdateBlockToolResultAsync(sessionId, callBlockNumber, emitOutput);

                        if (cleanKeys is not null)
                        {
                            var cleanedArgs = CleanToolArgs(args, cleanKeys);
                            if (cleanedArgs is not null)
                                await _store.UpdateBlockAsync(sessionId, callBlockNumber, content: cleanedArgs);
                        }

                        events.Add(new ToolResultTurnEvent(name, emitOutput));
                    }
                    // Memory clean: remove matching ChatMessages from context (by block numbers in args)
                    if (name == "memory" && output.StartsWith("Deleted "))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(args);
                            if (doc.RootElement.TryGetProperty("blocks", out var blk) && blk.ValueKind == JsonValueKind.Array)
                                ToolCallHelper.RemoveBlocksFromMessageList(contextMessages, blk.EnumerateArray().Select(e => e.GetDouble()));
                        }
                        catch { }
                    }
                    // Peek tools: replace ChatMessage with cleaned block render (ToContextString)
                    // Only tool blocks — reasoning peek blocks persist for the current turn
                    else if (isPeek)
                    {
                        var updatedBlock = await _store.GetBlockAsync(sessionId, callBlockNumber);
                        if (updatedBlock?.Type == BlockType.tool)
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
            events.AddRange(await FlushReasoning(agentOpts.PeekReasoning));
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
