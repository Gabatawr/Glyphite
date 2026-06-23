using Glyphite.Abstractions.Models;
using Glyphite.Host.Data;
using Xunit;

namespace Glyphite.Tests.Unit.Data;

public class BlockRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connStr;

    public BlockRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glyphite_test_{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        // SessionRepository creates sessions + blocks tables on init
        using var sessionRepo = new SessionRepository(_connStr);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch { /* best-effort cleanup */ }
    }

    private BlockRepository CreateBlockRepo() => new(_connStr);
    private SessionRepository CreateSessionRepo() => new(_connStr);

    [Fact]
    public async Task Can_Create_And_Retrieve_Block()
    {
        const string agentId = "test-agent-blocks";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        var blocks = new List<MemoryBlock>
        {
            new() { Type = BlockType.user_message, Content = "Hello, world!", Number = 1 },
            new() { Type = BlockType.agent_message, Content = "Hi there!", Number = 2, Model = "deepseek-chat" }
        };

        await repo.AppendBlocksAsync(agentId, blocks, nextNumber: 3);

        var loaded = await repo.LoadBlocksAsync(agentId);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(BlockType.user_message, loaded[0].Type);
        Assert.Equal("Hello, world!", loaded[0].Content);
        Assert.Equal(BlockType.agent_message, loaded[1].Type);
        Assert.Equal("Hi there!", loaded[1].Content);
        Assert.Equal("deepseek-chat", loaded[1].Model);
    }

    [Fact]
    public async Task Can_SoftDelete_Block()
    {
        const string agentId = "test-agent-softdel";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync(agentId,
            [new MemoryBlock { Type = BlockType.user_message, Content = "Delete me", Number = 1 }], nextNumber: 2);

        var (removed, protectedNums) = await repo.DeleteBlocksAsync(agentId, [1]);
        Assert.Equal(1, removed);
        Assert.Empty(protectedNums);

        var loaded = await repo.LoadBlocksAsync(agentId);
        Assert.Empty(loaded);

        var deletedBlock = await repo.GetBlockAsync(agentId, 1, includeDeleted: true);
        Assert.NotNull(deletedBlock);
        Assert.Equal("Delete me", deletedBlock!.Content);
    }


    [Fact]
    public async Task Blocks_List_Filtered_By_AgentId()
    {
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync("agent-a");
        await sessionRepo.EnsureSessionAsync("agent-b");

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync("agent-a",
            [MemoryBlock.Create(BlockType.user_message, "Agent A msg")], nextNumber: 2);
        await repo.AppendBlocksAsync("agent-b",
            [MemoryBlock.Create(BlockType.user_message, "Agent B msg")], nextNumber: 2);

        var agentABlocks = await repo.LoadBlocksAsync("agent-a");
        Assert.Single(agentABlocks);
        Assert.Equal("Agent A msg", agentABlocks[0].Content);

        var agentBBlocks = await repo.LoadBlocksAsync("agent-b");
        Assert.Single(agentBBlocks);
        Assert.Equal("Agent B msg", agentBBlocks[0].Content);
    }

    [Fact]
    public async Task Block_Types_Are_Properly_Stored()
    {
        const string agentId = "test-agent-types";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        var blocks = new List<MemoryBlock>
        {
            MemoryBlock.Create(BlockType.system_info, "sys info"),
            MemoryBlock.Create(BlockType.user_message, "user msg"),
            MemoryBlock.Create(BlockType.agent_reasoning, "thinking", model: "deepseek-chat"),
            MemoryBlock.Create(BlockType.agent_message, "agent msg"),
            MemoryBlock.Create(BlockType.tool, "tool call", toolName: "read_file"),
            MemoryBlock.SystemError("error occurred"),
            MemoryBlock.Create(BlockType.turn, "---"),
        };

        await repo.AppendBlocksAsync(agentId, blocks, nextNumber: 8);

        var loaded = await repo.LoadBlocksAsync(agentId);
        Assert.Equal(7, loaded.Count);
        Assert.Equal(BlockType.system_info, loaded[0].Type);
        Assert.Equal(BlockType.user_message, loaded[1].Type);
        Assert.Equal(BlockType.agent_reasoning, loaded[2].Type);
        Assert.Equal(BlockType.agent_message, loaded[3].Type);
        Assert.Equal(BlockType.tool, loaded[4].Type);
        Assert.Equal(BlockType.system_error, loaded[5].Type);
        Assert.Equal(BlockType.turn, loaded[6].Type);

        var stats = await repo.GetBlockTypeStatsAsync(agentId);
        Assert.Equal(7, stats.Values.Sum());
        Assert.True(stats.ContainsKey("system_info"));
        Assert.True(stats.ContainsKey("user_message"));
    }

    [Fact]
    public async Task GetBlockAsync_Returns_Null_For_Nonexistent()
    {
        const string agentId = "test-agent-nonexist";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        var block = await repo.GetBlockAsync(agentId, 999);
        Assert.Null(block);
    }

    [Fact]
    public async Task UpdateBlockContent_Works()
    {
        const string agentId = "test-agent-update";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync(agentId,
            [new MemoryBlock { Type = BlockType.user_message, Content = "Original content", Number = 1 }], nextNumber: 2);

        await repo.UpdateBlockContentAsync(agentId, 1, "Updated content");

        var updated = await repo.GetBlockAsync(agentId, 1);
        Assert.NotNull(updated);
        Assert.Equal("Updated content", updated!.Content);
    }

    [Fact]
    public async Task BlockCount_Works()
    {
        const string agentId = "test-agent-count";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        Assert.Equal(0, await repo.GetBlockCountAsync(agentId));

        await repo.AppendBlocksAsync(agentId,
            [MemoryBlock.Create(BlockType.user_message, "1")], nextNumber: 2);
        Assert.Equal(1, await repo.GetBlockCountAsync(agentId));

        await repo.AppendBlocksAsync(agentId,
            [MemoryBlock.Create(BlockType.agent_message, "2")], nextNumber: 3);
        Assert.Equal(2, await repo.GetBlockCountAsync(agentId));
    }

    [Fact]
    public async Task ClearAgentBlocks_Removes_All_Blocks()
    {
        const string agentId = "test-agent-clear";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync(agentId,
            [MemoryBlock.Create(BlockType.user_message, "1")], nextNumber: 2);
        await repo.AppendBlocksAsync(agentId,
            [MemoryBlock.Create(BlockType.agent_message, "2")], nextNumber: 3);

        Assert.Equal(2, await repo.GetBlockCountAsync(agentId));

        await repo.ClearAgentBlocksAsync(agentId);
        Assert.Equal(0, await repo.GetBlockCountAsync(agentId));
    }

    [Fact]
    public async Task LoadBlocksByType_Filters_Correctly()
    {
        const string agentId = "test-agent-loadbytype";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync(agentId, [
            MemoryBlock.Create(BlockType.user_message, "hello"),
            MemoryBlock.Create(BlockType.agent_message, "world", model: "m1"),
            MemoryBlock.Create(BlockType.user_message, "again"),
            MemoryBlock.Create(BlockType.agent_reasoning, "hmm", model: "m1"),
        ], nextNumber: 5);

        var userBlocks = await repo.LoadBlocksByTypeAsync(agentId, BlockType.user_message, null, false);
        Assert.Equal(2, userBlocks.Count);
        Assert.All(userBlocks, b => Assert.Equal(BlockType.user_message, b.Type));
    }
}
