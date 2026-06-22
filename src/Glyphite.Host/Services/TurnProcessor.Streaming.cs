using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>Per-turn state and streaming update processing.
/// Extracted from TurnProcessor.ProcessAsync local functions for clarity.</summary>
internal sealed class TurnContext
{
    private readonly IBlockStore _blockStore;
    private readonly IAgentStore _agentStore;
    private readonly ILogger _logger;

    public string SessionId { get; }
    public string ModelStr { get; }
    public double NextNum { get; set; }
    public StringBuilder ReasoningAccum { get; } = new();
    public StringBuilder TextAccum { get; } = new();
    public Dictionary<string, (string name, string args, bool isPeek, double blockNumber)> PendingToolCalls { get; } = new();
    public List<ChatMessage> ContextMessages { get; }
    public FailSafeChatClient FailSafeClient { get; }
    public AgentOptions AgentOpts { get; }

    private static readonly Dictionary<string, string[]> FileToolCleanArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = ["content"],
        ["write_file"] = ["content"],
        ["patch_file"] = ["newString", "oldString"]
    };

    public TurnContext(
        IBlockStore blockStore,
        IAgentStore agentStore,
        ILogger logger,
        string sessionId,
        string modelStr,
        double nextNum,
        List<ChatMessage> contextMessages,
        FailSafeChatClient failSafeClient,
        AgentOptions agentOpts)
    {
        _blockStore = blockStore;
        _agentStore = agentStore;
        _logger = logger;
        SessionId = sessionId;
        ModelStr = modelStr;
        NextNum = nextNum;
        ContextMessages = contextMessages;
        FailSafeClient = failSafeClient;
        AgentOpts = agentOpts;
    }

    public async Task<List<TurnEvent>> ProcessUpdate(ChatResponseUpdate update)
    {
        var events = new List<TurnEvent>();

        var text = string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
        var reasoning = string.Concat(update.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
        var fcc = update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
        var frc = update.Contents.OfType<FunctionResultContent>().FirstOrDefault();

        if (!string.IsNullOrEmpty(reasoning))
        {
            ReasoningAccum.Append(reasoning);
            events.Add(new ReasoningChunkEvent(reasoning));
        }

        if (!string.IsNullOrEmpty(text))
        {
            TextAccum.Append(text);
            events.Add(new TextChunkEvent(text));
        }

        if (fcc is not null)
        {
            await FlushReasoning(AgentOpts.PeekToolReasoning);
            await FlushText();

            var args = JsonSerializer.Serialize(fcc.Arguments ?? new Dictionary<string, object?>(),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            var isPeek = ToolCallHelper.IsPeekCall(fcc);

            var callBlock = MemoryBlock.ToolCall(fcc.Name, args, model: ModelStr);
            if (isPeek)
                callBlock.Data = new() { ["peek"] = true };
            callBlock.Number = NextNum++;
            await _blockStore.AppendBlocksAsync(SessionId, [callBlock], NextNum);

            var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");
            PendingToolCalls[callId] = (fcc.Name, args, isPeek, callBlock.Number);

            events.Add(new ToolCallTurnEvent(fcc.Name, args, isPeek));
        }

        if (frc is not null)
        {
            await FlushReasoning(AgentOpts.PeekToolReasoning);
            await FlushText();

            var output = frc.Result?.ToString() ?? "";
            var frcCallId = frc.CallId;
            if (frcCallId is not null && PendingToolCalls.TryGetValue(frcCallId, out var pending))
            {
                PendingToolCalls.Remove(frcCallId);
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
                    catch { _logger.LogWarning("Failed to parse args for path"); }

                    if (fPath is not null)
                    {
                        string fileContent;
                        if (name == "write_file")
                        {
                            try { fileContent = await File.ReadAllTextAsync(fPath); }
                            catch { _logger.LogWarning("Failed to read written file"); fileContent = output; /* fallback to raw output */ }
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

                async Task EmitToolResult(string emitOutput, string[]? cleanKeys)
                {
                    if (!string.IsNullOrEmpty(emitOutput))
                        await _blockStore.UpdateBlockToolResultAsync(SessionId, callBlockNumber, emitOutput);

                    if (cleanKeys is not null)
                    {
                        var cleanedArgs = CleanToolArgs(args, cleanKeys);
                        if (cleanedArgs is not null)
                            await _blockStore.UpdateBlockAsync(SessionId, callBlockNumber, content: cleanedArgs);
                    }

                    events.Add(new ToolResultTurnEvent(name, emitOutput));
                }
            }
            else
            {
                var block = MemoryBlock.ToolCall("unknown", output, model: ModelStr);
                block.Number = NextNum++;
                block.ToolResult = output;
                await _blockStore.AppendBlocksAsync(SessionId, [block], NextNum);
            }
        }

        return events;
    }

    public async Task FlushReasoning(bool isPeek)
    {
        if (ReasoningAccum.Length == 0) return;
        var fullReasoning = ReasoningAccum.ToString();
        ReasoningAccum.Clear();
        var block = MemoryBlock.AgentReasoning(fullReasoning, model: ModelStr);
        block.Number = NextNum++;
        if (isPeek)
            block.Data = new() { ["peek"] = true };
        await _blockStore.AppendBlocksAsync(SessionId, [block], NextNum);
    }

    public async Task FlushText()
    {
        if (TextAccum.Length == 0) return;
        var fullText = TextAccum.ToString();
        TextAccum.Clear();
        var block = MemoryBlock.AgentMessage(fullText, model: ModelStr);
        block.Number = NextNum++;
        await _blockStore.AppendBlocksAsync(SessionId, [block], NextNum);
    }

    public async Task FlushAll(AgentOptions agentOpts)
    {
        await FlushReasoning(agentOpts.PeekReasoning);
        await FlushText();
    }

    public string? CleanToolArgs(string argsJson, params string[] keys)
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CleanToolArgs failed");
            return null;
        }
    }

    public static string BuildPeekCleanMessage(int total, Dictionary<string, int> stats)
    {
        var lines = new List<string> { $"── Cleaned {total} peek blocks ─────────────────" };
        foreach (var kv in stats.OrderByDescending(kv => kv.Value))
        {
            var icon = BlockTypeIcon.Get(kv.Key);
            lines.Add($"  {icon} {kv.Key,-20}: {kv.Value,4}");
        }
        return string.Join('\n', lines);
    }
}
