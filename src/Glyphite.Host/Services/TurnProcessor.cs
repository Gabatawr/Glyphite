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
    private readonly DeepSeekOptions _deepseek;
    private readonly AgentOptions _agentOpts;
    private readonly ToolStreamingOptions _streamOpts;
    public TurnProcessor(
        IMemoryStore store,
        IBlockMemoryProvider blockMemory,
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        IConfigService cfgService,
        IOptions<DeepSeekOptions> deepseek,
        IOptions<AgentOptions> agentOpts,
        IOptions<ToolStreamingOptions> streamOpts)
    {
        _store = store;
        _blockMemory = blockMemory;
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _cfgService = cfgService;
        _deepseek = deepseek.Value;
        _agentOpts = agentOpts.Value;
        _streamOpts = streamOpts.Value;
    }

    private double _nextNum;
    private string _sessionId = string.Empty;
    private string _modelStr = string.Empty;
    private readonly StringBuilder _reasoningAccum = new();
    private readonly StringBuilder _textAccum = new();
    private readonly List<(string name, string args, bool isPeek, double blockNumber)> _pendingToolCalls = [];

    public async IAsyncEnumerable<TurnEvent> ProcessAsync(
        string sessionId,
        string input,
        ChatOptions chatOptions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _sessionId = sessionId;
        _modelStr = chatOptions.ModelId ?? _deepseek.Model;

        chatOptions.Tools = _toolRegistry.GetBuiltinTools(sessionId).ToList();

        // Get peek block stats before cleaning (for informative message)
        var peekStats = await _store.GetPeekBlockStatsAsync(sessionId);
        var peekCleaned = await _store.RemovePeekBlocksAsync(sessionId);

        var contextMessages = await _blockMemory.BuildContextAsync(
            sessionId, _modelStr, _deepseek.ContextWindow);

        _nextNum = await _store.GetNextNumberAsync(sessionId);
        if (_nextNum <= 0) _nextNum = 1;

        // Auto-tool: peek cleanup — visible to user AND model (peek block, cleaned next turn)
        if (peekCleaned > 0)
        {
            var peekMsg = BuildPeekCleanMessage(peekCleaned, peekStats);
            var cleanArgs = $"{{\"count\":{peekCleaned}}}";
            yield return new AutoToolTurnEvent("peek_clean", cleanArgs, false, peekMsg);

            var autoBlock = MemoryBlock.AutoTool("peek_clean", cleanArgs, peekMsg, _modelStr);
            autoBlock.Number = _nextNum++;
            await _store.AppendBlocksAsync(_sessionId, [autoBlock], _nextNum);

            contextMessages.Add(new ChatMessage(ChatRole.System, autoBlock.ToContextString()));
        }

        var initialMessages = new List<ChatMessage>();
        initialMessages.AddRange(contextMessages);
        initialMessages.Add(new ChatMessage(ChatRole.User, input));

        var failSafeClient = new FailSafeChatClient(
            _chatClient, _agentOpts.MaxToolIterations, _streamOpts);

        _blockMemory.CurrentExecutedIds.Value = failSafeClient.ExecutedCallIds;

        var userBlock = MemoryBlock.UserMessage(input);
        userBlock.Number = _nextNum++;
        await _store.AppendBlocksAsync(sessionId, [userBlock], _nextNum);

        await foreach (var update in failSafeClient
            .GetStreamingResponseAsync(initialMessages, chatOptions)
            .WithCancellation(ct))
        {
            var events = await ProcessUpdateAsync(update);
            foreach (var e in events)
                yield return e;
        }

        // Persist usage from all iterations
        await _store.RecordUsageAsync(sessionId, failSafeClient.TotalCacheHitTokens, failSafeClient.TotalCacheMissTokens, failSafeClient.TotalOutputTokens, failSafeClient.LastHitTokens, failSafeClient.LastMissTokens);
        yield return new UsageTurnEvent(failSafeClient.TotalCacheHitTokens, failSafeClient.TotalCacheMissTokens, failSafeClient.TotalOutputTokens, failSafeClient.LastHitTokens, failSafeClient.LastMissTokens);

        var flushEvents = await FlushAllAsync();
        foreach (var e in flushEvents)
            yield return e;

    }

    private async Task<List<TurnEvent>> ProcessUpdateAsync(ChatResponseUpdate update)
    {
        var events = new List<TurnEvent>();

        var text = string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
        var reasoning = string.Concat(update.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
        var fcc = update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
        var frc = update.Contents.OfType<FunctionResultContent>().FirstOrDefault();

        if (!string.IsNullOrEmpty(reasoning))
        {
            _reasoningAccum.Append(reasoning);
            events.Add(new ReasoningChunkEvent(reasoning));
        }

        if (!string.IsNullOrEmpty(text))
        {
            _textAccum.Append(text);
            events.Add(new TextChunkEvent(text));
        }

        if (fcc is not null)
        {
            events.AddRange(await FlushReasoningAsync(_agentOpts.PeekToolReasoning));
            events.AddRange(await FlushTextAsync());

            var args = JsonSerializer.Serialize(fcc.Arguments ?? new Dictionary<string, object?>(),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            var isPeek = fcc.Arguments?.TryGetValue("peek", out var peekVal) == true &&
                (peekVal is bool pb ? pb : (peekVal is JsonElement je && je.ValueKind == JsonValueKind.True));

            var callBlock = MemoryBlock.ToolCall(fcc.Name, args, model: _modelStr);
            if (isPeek)
                callBlock.Data = new() { ["peek"] = true };
            callBlock.Number = _nextNum++;
            await _store.AppendBlocksAsync(_sessionId, [callBlock], _nextNum);

            _pendingToolCalls.Add((fcc.Name, args, isPeek, callBlock.Number));

            events.Add(new ToolCallTurnEvent(fcc.Name, args, isPeek));
        }

        if (frc is not null)
        {
            events.AddRange(await FlushReasoningAsync(_agentOpts.PeekToolReasoning));
            events.AddRange(await FlushTextAsync());

            var output = frc.Result?.ToString() ?? "";
            if (_pendingToolCalls.Count > 0)
            {
                var (name, args, isPeek, callBlockNumber) = _pendingToolCalls[0];
                _pendingToolCalls.RemoveAt(0);

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

                        var fileBlock = MemoryBlock.FileBlock(fileContent, fPath);
                        fileBlock.Number = _nextNum++;
                        if (isPeek)
                            fileBlock.Data = new() { ["peek"] = true };
                        await _store.AppendBlocksAsync(_sessionId, [fileBlock], _nextNum);

                        var cleanedArgs = CleanToolArgs(args, "content");
                        if (!isPeek)
                            await _store.UpdateBlockToolResultAsync(_sessionId, callBlockNumber, "");
                        if (cleanedArgs is not null)
                            await _store.UpdateBlockAsync(_sessionId, callBlockNumber, content: cleanedArgs);

                        events.Add(new FileBlockTurnEvent(fileContent, fPath));
                    }
                    else
                    {
                        events.Add(new ToolResultTurnEvent(name, output));
                    }
                }
                else if (name == "patch_file")
                {
                    if (!isPeek && !string.IsNullOrEmpty(output))
                        await _store.UpdateBlockToolResultAsync(_sessionId, callBlockNumber, output);

                    var cleanedArgs = CleanToolArgs(args, "newString", "oldString");
                    if (cleanedArgs is not null)
                        await _store.UpdateBlockAsync(_sessionId, callBlockNumber, content: cleanedArgs);

                    events.Add(new ToolResultTurnEvent(name, output));
                }
                else
                {
                    if (!isPeek && !string.IsNullOrEmpty(output))
                        await _store.UpdateBlockToolResultAsync(_sessionId, callBlockNumber, output);

                    events.Add(new ToolResultTurnEvent(name, output));
                }
            }
            else
            {
                var block = MemoryBlock.ToolCall("unknown", output, model: _modelStr);
                block.Number = _nextNum++;
                block.ToolResult = output;
                await _store.AppendBlocksAsync(_sessionId, [block], _nextNum);
            }
        }

        return events;
    }

    private static string BuildPeekCleanMessage(int total, Dictionary<string, int> stats)
    {
        var iconMap = new Dictionary<string, string>
        {
            ["user_message"] = "👤", ["agent_message"] = "💬", ["agent_reasoning"] = "🧠",
            ["tool"] = "🔧", ["todo"] = "📋", ["todo_update"] = "🔄",
            ["file"] = "📄", ["auto_tool"] = "🤖"
        };
        var lines = new List<string> { $"── Cleaned {total} peek blocks ─────────────────" };
        foreach (var kv in stats.OrderByDescending(kv => kv.Value))
        {
            var icon = iconMap.GetValueOrDefault(kv.Key, "  ");
            lines.Add($"  {icon} {kv.Key,-20}: {kv.Value,4}");
        }
        return string.Join('\n', lines);
    }

    private async Task<List<TurnEvent>> FlushReasoningAsync(bool isPeek)
    {
        if (_reasoningAccum.Length == 0) return [];
        var fullReasoning = _reasoningAccum.ToString();
        _reasoningAccum.Clear();
        var block = MemoryBlock.AgentReasoning(fullReasoning, model: _modelStr);
        block.Number = _nextNum++;
        if (isPeek)
            block.Data = new() { ["peek"] = true };
        await _store.AppendBlocksAsync(_sessionId, [block], _nextNum);
        return []; // content already streamed via ReasoningChunkEvent
    }

    private Task<List<TurnEvent>> FlushTextAsync()
    {
        if (_textAccum.Length == 0) return Task.FromResult<List<TurnEvent>>([]);
        var fullText = _textAccum.ToString();
        _textAccum.Clear();
        var block = MemoryBlock.AgentMessage(fullText, model: _modelStr);
        block.Number = _nextNum++;
        return _store.AppendBlocksAsync(_sessionId, [block], _nextNum).ContinueWith(_ =>
            (List<TurnEvent>)[]); // content already streamed via TextChunkEvent
    }

    private async Task<List<TurnEvent>> FlushAllAsync()
    {
        var events = new List<TurnEvent>();
        events.AddRange(await FlushReasoningAsync(_agentOpts.PeekReasoning));
        events.AddRange(await FlushTextAsync());
        return events;
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
