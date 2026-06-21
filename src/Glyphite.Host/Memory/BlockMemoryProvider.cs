using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SharpToken;

namespace Glyphite.Host.Memory;

public class BlockMemoryState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double NextNumber { get; set; } = 1;
    public List<MemoryBlock> Blocks { get; set; } = [];
    public string? CurrentModel { get; set; }
}

public partial class BlockMemoryProvider : AIContextProvider, IBlockMemoryProvider
{
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private readonly IConfigService _cfgService;
    private readonly ProviderSessionState<BlockMemoryState> _sessionState;
    private readonly string? _defaultModel;
    private readonly MemoryOptions _memOpts;
    private readonly AgentOptions _agentOpts;
    private readonly CompressionOptions? _compOpts;
    private string? _defaultSessionId;

    private static readonly GptEncoding? _encoding = GetEncoding();

    private static GptEncoding? GetEncoding()
    {
        try { return GptEncoding.GetEncoding("cl100k_base"); }
        catch { return null; }
    }

    public static int EstimateTokens(string text)
    {
        if (_encoding is null || string.IsNullOrEmpty(text)) return 0;
        return _encoding.Encode(text).Count;
    }

    public AsyncLocal<HashSet<string>?> CurrentExecutedIds { get; } = new();

    public string? AgentFilePath { get; set; }

    public BlockMemoryProvider(IAgentStore agentStore, IBlockStore blockStore, IConfigService cfgService, MemoryOptions memOpts, AgentOptions agentOpts, string? defaultModel = null, CompressionOptions? compOpts = null)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _cfgService = cfgService;
        _defaultModel = defaultModel;
        _memOpts = memOpts;
        _agentOpts = agentOpts;
        _compOpts = compOpts;
        _sessionState = new ProviderSessionState<BlockMemoryState>(
            _ => new BlockMemoryState(),
            GetType().Name);
    }

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    // ── Instance API ──

    public async Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id)
        => (await _blockStore.LoadBlocksAsync(id)).AsReadOnly();

    public async Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id, BlockType? type = null, int? limit = null, bool desc = true)
        => (await _blockStore.LoadBlocksByTypeAsync(id, type, limit, desc)).AsReadOnly();

    public async Task<MemoryBlock?> GetBlockAsync(string id, double number, bool includeDeleted = false)
        => await _blockStore.GetBlockAsync(id, number, includeDeleted);

    public async Task UpdateBlockDataAsync(string sessionId, double number, Dictionary<string, object>? data)
        => await _blockStore.UpdateBlockDataAsync(sessionId, number, data);

    public async Task<int> RemoveBlocksAsync(string sessionId, Predicate<MemoryBlock> match)
        => await _blockStore.RemoveBlocksAsync(sessionId, match);

    public async Task<string> DeleteBlocksAsync(string sessionId, double[] numbers, bool cascade = true)
    {
        if (!await _agentStore.AgentExistsAsync(sessionId))
            return $"Session '{sessionId}' not found";

        var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>("Memory", sessionId);
        var protectedTypes = new HashSet<BlockType>(
            memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));
        var (removed, protectedNums) = await _blockStore.DeleteBlocksAsync(sessionId, numbers, protectedTypes, cascade);
        var msg = $"Deleted {removed} block{(removed == 1 ? "" : "s")}";
        if (protectedNums.Count > 0)
            msg += $"; skipped protected block{(protectedNums.Count == 1 ? "" : "s")}: {string.Join(", ", protectedNums)}";
        return msg;
    }

    public async Task<int> RecoverBlocksAsync(string sessionId, double[] numbers, bool cascade = false)
        => await _blockStore.RecoverBlocksAsync(sessionId, numbers, cascade);

    public async Task<string> DeleteBlocksByFilterAsync(string sessionId, string[]? types, string? recent)
    {
        if (!await _agentStore.AgentExistsAsync(sessionId))
            return $"Agent '{sessionId}' not found";

        TimeSpan? ts = null;
        if (recent is not null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(recent.Trim(), @"^(\d+)\s*(h|hour|hours|m|min|minute|minutes|d|day|days)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success)
                return $"Invalid recent format: '{recent}'. Use e.g. '1h', '30m', '1d'.";
            var val = int.Parse(m.Groups[1].Value);
            ts = m.Groups[2].Value[0] switch
            {
                'h' => TimeSpan.FromHours(val),
                'm' => TimeSpan.FromMinutes(val),
                'd' => TimeSpan.FromDays(val),
                _ => null
            };
        }

        var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>("Memory", sessionId);
        var protectedTypes = new HashSet<BlockType>(
            memOpts.ProtectedBlockTypes.Select(t => Enum.Parse<BlockType>(t, ignoreCase: true)));
        var removed = await _blockStore.DeleteBlocksByFilterAsync(sessionId, types, ts, protectedTypes);
        if (removed == 0)
            return "No matching blocks found to delete.";
        return $"Deleted {removed} block{(removed == 1 ? "" : "s")}.";
    }

    public async Task<bool> AgentExistsAsync(string sessionId)
        => await _agentStore.AgentExistsAsync(sessionId);

    public async Task<bool> SetAgentModelAsync(string sessionId, string model)
        => await _agentStore.SetAgentModelAsync(sessionId, model);

    public async Task<string?> GetAgentModelAsync(string sessionId)
        => await _agentStore.GetAgentModelAsync(sessionId);

    public async Task<string?> GetModelAsync(string id)
        => await _agentStore.GetAgentModelAsync(id) ?? _defaultModel;

    // ── Error storage ──

    public async Task StoreErrorAsync(string sessionId, string error)
    {
        var nextNum = await _agentStore.GetNextNumberAsync(sessionId);
        if (nextNum <= 0) nextNum = 1;
        var block = MemoryBlock.SystemError(error);
        block.Number = nextNum;
        await _blockStore.AppendBlocksAsync(sessionId, [block], nextNum + 1);
    }

    public async Task<(long Hit, long Miss, long Output)> GetUsageAsync(string sessionId)
        => await _agentStore.GetUsageAsync(sessionId);

    // ── ID lifecycle ──

    public async Task<string> GetOrCreateIdAsync(AgentSession? session)
    {
        var state = _sessionState.GetOrInitializeState(session);
        state.CurrentModel ??= _defaultModel;
        await _agentStore.EnsureSessionAsync(state.Id);
        if (_defaultModel is not null && await _agentStore.GetAgentModelAsync(state.Id) is null)
            await _agentStore.SetAgentModelAsync(state.Id, _defaultModel);
        return state.Id;
    }

    public async Task<string> GetOrCreateIdAsync()
    {
        _defaultSessionId ??= Guid.NewGuid().ToString("N");
        await _agentStore.EnsureSessionAsync(_defaultSessionId);
        if (_defaultModel is not null && await _agentStore.GetAgentModelAsync(_defaultSessionId) is null)
            await _agentStore.SetAgentModelAsync(_defaultSessionId, _defaultModel);
        return _defaultSessionId;
    }

    // ── AIContextProvider lifecycle ──

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken ct)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        state.CurrentModel ??= _defaultModel;

        await _agentStore.EnsureSessionAsync(state.Id);

        var blocks = await _blockStore.LoadBlocksAsync(state.Id);
        if (blocks.Count > 0)
        {
            state.Blocks = blocks;
            state.NextNumber = blocks.Max(b => b.Number) + 1;
        }

        if (state.Blocks.Count == 0)
        {
            var agentData = MemoryBlock.AgentData("agent", _agentOpts.AgentName);
            agentData.Number = state.NextNumber++;
            state.Blocks.Add(agentData);
            await _blockStore.AppendBlocksAsync(state.Id, state.Blocks, state.NextNumber);
        }

        var messages = state.Blocks.Select(b =>
            new ChatMessage(ChatRole.System, b.ToContextString()));

        _sessionState.SaveState(context.Session, state);
        return new AIContext { Messages = messages };
    }

    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken ct)
    {
        // Blocks are already saved incrementally in ChatRepl.ProcessInputAsync.
        // This method would duplicate them, so we skip DB writes here.
        // We still update the in-memory state so the NextNumber is consistent
        // for subsequent turns.
        var state = _sessionState.GetOrInitializeState(context.Session);

        if (context.RequestMessages is { } reqMsgs)
        {
            foreach (var msg in reqMsgs)
            {
                if (msg.Role == ChatRole.User && !string.IsNullOrEmpty(msg.Text))
                    AddBlock(state, MemoryBlock.UserMessage(msg.Text));
            }
        }

        if (context.ResponseMessages is { } resMsgs)
        {
            foreach (var msg in resMsgs)
            {
                if (msg.Role == ChatRole.Assistant)
                {
                    foreach (var content in msg.Contents)
                    {
                        if (content is TextReasoningContent r && !string.IsNullOrEmpty(r.Text))
                            AddBlock(state, MemoryBlock.AgentReasoning(r.Text, model: state.CurrentModel));
                        else if (content is TextContent t && !string.IsNullOrEmpty(t.Text))
                            AddBlock(state, MemoryBlock.AgentMessage(t.Text, model: state.CurrentModel));
                    }
                }
            }
        }

        _sessionState.SaveState(context.Session, state);
    }

    // ── Helpers ──

    private static void AddBlock(BlockMemoryState state, MemoryBlock block)
    {
        block.Number = state.NextNumber++;
        state.Blocks.Add(block);
    }

    private static int ExtractInt(object? val)
    {
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.GetInt32();
        if (val is int i) return i;
        return -1;
    }
}
