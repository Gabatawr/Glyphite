using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;

namespace Glyphite.Host.Memory;

/// <summary>
/// The instance API over the block and agent stores. Everything that builds the context lives in
/// <c>BlockMemoryProvider.Context.cs</c> and the counters in <c>BlockMemoryProvider.Metrics.cs</c>.
///
/// Deliberately <b>not</b> an <c>AIContextProvider</c>: the <c>ChatClientAgent</c> pipeline it would
/// plug into is not used anywhere, and its lifecycle would store the same blocks
/// <c>TurnProcessor</c> already writes — so the whole path was dead code that would duplicate data if
/// it were ever switched on.
/// </summary>
public partial class BlockMemoryProvider : IBlockMemoryProvider
{
    private readonly IAgentStore _agentStore;
    private readonly IBlockStore _blockStore;
    private readonly IConfigService _config;
    private readonly string? _defaultModel;

    public AsyncLocal<HashSet<string>?> CurrentExecutedIds { get; } = new();

    public BlockMemoryProvider(
        IAgentStore agentStore,
        IBlockStore blockStore,
        IConfigService config,
        string? defaultModel = null)
    {
        _agentStore = agentStore;
        _blockStore = blockStore;
        _config = config;
        _defaultModel = defaultModel;
    }

    // ── Instance API ──

    public async Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id)
        => (await _blockStore.LoadBlocksAsync(id)).AsReadOnly();

    public async Task<IReadOnlyList<MemoryBlock>> GetBlocksAsync(string id, BlockType? type = null, int? limit = null, bool desc = true)
        => (await _blockStore.LoadBlocksByTypeAsync(id, type, limit, desc)).AsReadOnly();

    public async Task<MemoryBlock?> GetBlockAsync(string id, double number, bool includeDeleted = false)
        => await _blockStore.GetBlockAsync(id, number, includeDeleted);

    public async Task UpdateBlockDataAsync(string agentId, double number, Dictionary<string, object>? data)
        => await _blockStore.UpdateBlockDataAsync(agentId, number, data);

    public async Task<int> RemoveBlocksAsync(string agentId, Predicate<MemoryBlock> match)
        => await _blockStore.RemoveBlocksAsync(agentId, match);

    public async Task<bool> AgentExistsAsync(string agentId)
        => await _agentStore.AgentExistsAsync(agentId);

    public async Task<bool> SetAgentModelAsync(string agentId, string model)
        => await _agentStore.SetAgentModelAsync(agentId, model);

    public async Task<string?> GetAgentModelAsync(string agentId)
        => await _agentStore.GetAgentModelAsync(agentId);

    public async Task<string?> GetModelAsync(string id)
        => await _agentStore.GetAgentModelAsync(id) ?? _defaultModel;

    // ── Error storage ──

    public async Task StoreErrorAsync(string agentId, string error)
    {
        await _blockStore.AppendBlocksAsync(agentId, [MemoryBlock.SystemError(error)]);
    }

    public async Task<(long Hit, long Miss, long Output, long Reasoning)> GetUsageAsync(string agentId)
        => await _agentStore.GetUsageAsync(agentId);
}
