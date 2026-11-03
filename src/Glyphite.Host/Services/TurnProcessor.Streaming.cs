using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Glyphite.Host.Tools;

namespace Glyphite.Host.Services;

/// <summary>Per-turn streaming state and update processing.
/// Extracted from TurnProcessor.ProcessAsync local functions for clarity.
/// Responsibility: track per-turn state (accumulators, pending tool calls, next block number)
/// and persist blocks as updates arrive. Usage tracking lives in <see cref="FailSafeChatClient"/>.</summary>
internal sealed class TurnContext
{
    private readonly IBlockStore _blockStore;
    private readonly IAgentStore _agentStore;
    private readonly ILogger _logger;
    private readonly int _writeFileMaxSize;
    private readonly string? _baseDir;
    private readonly IReadOnlyList<AITool>? _tools;

    /// <summary>The single config-wrapper parameter — execution plumbing, never a tool argument.</summary>
    private const string ExtraCfgParamName = "extra_cfg";

    private static readonly string TmpDir = Path.Combine(AppContext.BaseDirectory, "tmp");

    public string SessionId { get; }
    public string ModelStr { get; }
    public StringBuilder ReasoningAccum { get; } = new();
    public StringBuilder TextAccum { get; } = new();
    public Dictionary<string, (string name, string args, bool isPeek, double blockNumber)> PendingToolCalls { get; } = new();
    public FailSafeChatClient FailSafeClient { get; }

    /// <summary>Blocks the model itself wrote during this turn — reasoning, answer text and tool-call
    /// arguments. Their rendered size is what the turn actually occupies in the stored history, which
    /// is a different number from the billed completion count: the provider charges for thinking it
    /// never hands back, so billing is always larger. The turn marker records both.</summary>
    public List<MemoryBlock> StoredBlocks { get; } = [];

    private static readonly Dictionary<string, string[]> FileToolCleanArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = ["content"],
        ["write_file"] = ["content"],
        ["patch_file"] = ["new", "old"]
    };

    public TurnContext(
        IBlockStore blockStore,
        IAgentStore agentStore,
        ILogger logger,
        string sessionId,
        string modelStr,
        FailSafeChatClient failSafeClient,
        int writeFileMaxSize = ToolExecutionDefaults.ContentMaxSize,
        string? baseDir = null,
        IReadOnlyList<AITool>? tools = null)
    {
        _blockStore = blockStore;
        _agentStore = agentStore;
        _logger = logger;
        SessionId = sessionId;
        ModelStr = modelStr;
        FailSafeClient = failSafeClient;
        _writeFileMaxSize = writeFileMaxSize;
        _baseDir = baseDir;
        _tools = tools;
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
            await FlushReasoning();
            await FlushText();

            var args = JsonSerializer.Serialize(fcc.Arguments ?? new Dictionary<string, object?>(),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            var isPeek = ToolCallHelper.IsPeekCall(fcc);

            var callBlock = MemoryBlock.ToolCall(fcc.Name, args, model: ModelStr);
            if (isPeek)
                callBlock.Data = new() { ["peek"] = true };
            await _blockStore.AppendBlocksAsync(SessionId, [callBlock]);
            StoredBlocks.Add(callBlock);

            var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");
            PendingToolCalls[callId] = (fcc.Name, args, isPeek, callBlock.Number);

            events.Add(new ToolCallTurnEvent(fcc.Name, args, isPeek));
        }

        if (frc is not null)
        {
            await FlushReasoning();
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
                            try
                            {
                                // Same resolution rule as write_file itself: a relative path is relative to
                                // the agent's directory, not to whatever directory glyphite was launched in.
                                fileContent = await File.ReadAllTextAsync(OSHelper.ResolvePath(fPath, _baseDir));
                            }
                            catch { _logger.LogWarning("Failed to read written file"); fileContent = output; /* fallback to raw output */ }
                            // The decorator truncates the tool's returned string, but we re-read the file
                            // to show the LLM what was written — apply the same limit so context can't blow up.
                            fileContent = ToolConfigDecorator.Truncate(fileContent, _writeFileMaxSize, TmpDir, name, SessionId);
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
                    var cleanedArgs = BuildStoredArgs(name, args, cleanKeys);

                    if (!string.IsNullOrEmpty(emitOutput))
                        await _blockStore.UpdateBlockToolResultAsync(SessionId, callBlockNumber, emitOutput);

                    if (cleanedArgs is not null)
                        await _blockStore.UpdateBlockAsync(SessionId, callBlockNumber, content: cleanedArgs);

                    // Keep the in-memory copy in step with the store: the turn marker measures these
                    // blocks after the turn is over and must record the sizes that were persisted.
                    var stored = StoredBlocks.FirstOrDefault(b => b.Number == callBlockNumber);
                    if (stored is not null)
                    {
                        if (!string.IsNullOrEmpty(emitOutput))
                            stored.ToolResult = emitOutput;
                        if (cleanedArgs is not null)
                            stored.Content = cleanedArgs;
                    }

                    events.Add(new ToolResultTurnEvent(name, emitOutput));
                }
            }
            else
            {
                var block = MemoryBlock.ToolCall("unknown", output, model: ModelStr);
                block.ToolResult = output;
                await _blockStore.AppendBlocksAsync(SessionId, [block]);
                StoredBlocks.Add(block);
            }
        }

        return events;
    }

    public async Task FlushReasoning()
    {
        if (ReasoningAccum.Length == 0) return;
        var fullReasoning = ReasoningAccum.ToString();
        ReasoningAccum.Clear();
        var block = MemoryBlock.AgentReasoning(fullReasoning, model: ModelStr);
        await _blockStore.AppendBlocksAsync(SessionId, [block]);
        StoredBlocks.Add(block);
    }

    public async Task FlushText()
    {
        if (TextAccum.Length == 0) return;
        var fullText = TextAccum.ToString();
        TextAccum.Clear();
        var block = MemoryBlock.AgentMessage(fullText, model: ModelStr);
        await _blockStore.AppendBlocksAsync(SessionId, [block]);
        StoredBlocks.Add(block);
    }

    public async Task FlushAll()
    {
        await FlushReasoning();
        await FlushText();
    }

    /// <summary>Arguments as they are stored in the block. Two kinds of noise are dropped: bulk payload
    /// that already lives in the tool result (<c>content</c>, patch halves), and <c>extra_cfg</c> fields
    /// that merely restate the tool's own defaults. A real override stays — "ran with a 5s timeout" is
    /// part of how the call was made, while "peek: true" on a tool that peeks by default is not.
    /// Returns null when nothing changed, so the caller can skip the write.</summary>
    public string? BuildStoredArgs(string name, string argsJson, string[]? cleanKeys)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var props = new Dictionary<string, JsonElement?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                props[prop.Name] = prop.Value;

            foreach (var key in cleanKeys ?? [])
                props.Remove(key);

            // No tool instance means no defaults to compare against (a tool that went away mid-turn);
            // then extra_cfg is kept as passed rather than hiding something we cannot judge.
            if (props.TryGetValue(ExtraCfgParamName, out var cfg)
                && cfg is { ValueKind: JsonValueKind.Object }
                && FindConfigTool(name) is { } configTool)
            {
                var overrides = configTool.OverridesFrom(cfg.Value);
                if (overrides.Count == 0)
                    props.Remove(ExtraCfgParamName);
                else
                    props[ExtraCfgParamName] = JsonSerializer.SerializeToElement(overrides);
            }

            var cleaned = JsonSerializer.Serialize(
                props.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            return cleaned == argsJson ? null : cleaned;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuildStoredArgs failed");
            return null;
        }
    }

    private ToolConfigDecorator? FindConfigTool(string name) =>
        _tools?.OfType<ToolConfigDecorator>()
              .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));


}
