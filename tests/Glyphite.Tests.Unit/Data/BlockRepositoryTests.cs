using Glyphite.Abstractions.Models;
using Glyphite.Host.Data;
using Glyphite.Tests.Unit.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glyphite.Tests.Unit.Data;

public class BlockRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private BlockRepository CreateBlockRepo() => new(_db.ConnStr);
    private SessionRepository CreateSessionRepo() => new(_db.ConnStr);

    /// <summary>
    /// The read behind every turn is <c>WHERE agent_id = ? AND is_deleted = 0 ORDER BY number</c>. The
    /// index has to end in <c>number</c>, otherwise SQLite sorts the result on every single turn; the
    /// old two-column index is a prefix of it and is dropped instead of being maintained for nothing.
    /// </summary>
    [Fact]
    public void Initialize_IndexesTheMainRead()
    {
        using var repo = CreateBlockRepo();
        using var conn = new SqliteConnection(_db.ConnStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_info('idx_blocks_agent_deleted_number')";
        using (var reader = cmd.ExecuteReader())
        {
            var columns = new List<string>();
            while (reader.Read())
                columns.Add(reader.GetString(2)); // PRAGMA index_info: seqno, cid, name

            Assert.Equal(new[] { "agent_id", "is_deleted", "number" }, columns);
        }

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_blocks_agent_deleted'";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }
// ── CRUD ──

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

        await repo.AppendBlocksAsync(agentId, blocks);

        var loaded = await repo.LoadBlocksAsync(agentId);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(BlockType.user_message, loaded[0].Type);
        Assert.Equal("Hello, world!", loaded[0].Content);
        Assert.Equal(BlockType.agent_message, loaded[1].Type);
        Assert.Equal("Hi there!", loaded[1].Content);
        Assert.Equal("deepseek-chat", loaded[1].Model);
    }
// ── Queries ──

    [Fact]
    public async Task Blocks_List_Filtered_By_AgentId()
    {
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync("agent-a");
        await sessionRepo.EnsureSessionAsync("agent-b");

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync("agent-a",
            [MemoryBlock.Create(BlockType.user_message, "Agent A msg")]);
        await repo.AppendBlocksAsync("agent-b",
            [MemoryBlock.Create(BlockType.user_message, "Agent B msg")]);

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

        await repo.AppendBlocksAsync(agentId, blocks);

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
            [new MemoryBlock { Type = BlockType.user_message, Content = "Original content", Number = 1 }]);

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
            [MemoryBlock.Create(BlockType.user_message, "1")]);
        Assert.Equal(1, await repo.GetBlockCountAsync(agentId));

        await repo.AppendBlocksAsync(agentId,
            [MemoryBlock.Create(BlockType.agent_message, "2")]);
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
            [MemoryBlock.Create(BlockType.user_message, "1")]);
        await repo.AppendBlocksAsync(agentId,
            [MemoryBlock.Create(BlockType.agent_message, "2")]);

        Assert.Equal(2, await repo.GetBlockCountAsync(agentId));

        await repo.ClearAgentBlocksAsync(agentId);
        Assert.Equal(0, await repo.GetBlockCountAsync(agentId));
    }

    // ── Numbering: the block store is the only allocator ──

    [Fact]
    public async Task AppendBlocks_AssignsSequentialNumbers_AndAdvancesCounter()
    {
        const string agentId = "test-agent-alloc";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        var first = new List<MemoryBlock>
        {
            MemoryBlock.Create(BlockType.user_message, "one"),
            MemoryBlock.Create(BlockType.agent_message, "two"),
        };
        await repo.AppendBlocksAsync(agentId, first);

        Assert.Equal([1.0, 2.0], first.Select(b => b.Number));
        Assert.Equal(3, await sessionRepo.GetNextNumberAsync(agentId));

        var second = new List<MemoryBlock> { MemoryBlock.Create(BlockType.user_message, "three") };
        await repo.AppendBlocksAsync(agentId, second);

        Assert.Equal(3, second[0].Number);
        Assert.Equal(4, await sessionRepo.GetNextNumberAsync(agentId));
    }

    [Fact]
    public async Task AppendBlocks_StaleCounter_NeverReusesAnExistingNumber()
    {
        const string agentId = "test-agent-stale";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();

        // History numbered 10..11 while the session counter says 1 — the state a fork used to leave
        // behind. Allocation must follow the blocks, not the counter.
        var seeded = new List<MemoryBlock>
        {
            MemoryBlock.Create(BlockType.user_message, "old one"),
            MemoryBlock.Create(BlockType.user_message, "old two"),
        };
        seeded[0].Number = 10;
        seeded[1].Number = 11;
        await repo.ReplaceBlocksSinceAsync(agentId, fromNumber: 0, seeded, nextNumber: 1);

        var appended = MemoryBlock.Create(BlockType.agent_message, "new");
        await repo.AppendBlocksAsync(agentId, [appended]);

        Assert.Equal(12, appended.Number);
        Assert.Equal(13, await sessionRepo.GetNextNumberAsync(agentId));
    }

    [Fact]
    public async Task AppendBlocks_SoftDeletedNumber_IsNotReused()
    {
        const string agentId = "test-agent-reuse";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync(agentId, [
            MemoryBlock.Create(BlockType.user_message, "keep"),
            MemoryBlock.Create(BlockType.user_message, "delete me"),
        ]);
        await repo.RemoveBlocksAsync(agentId, b => b.Content == "delete me");

        var appended = MemoryBlock.Create(BlockType.agent_message, "after delete");
        await repo.AppendBlocksAsync(agentId, [appended]);

        // A removed block still owns its number: 3 is the next free one, not 2.
        Assert.Equal(3, appended.Number);
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
        ]);

        var userBlocks = await repo.LoadBlocksByTypeAsync(agentId, BlockType.user_message, null, false);
        Assert.Equal(2, userBlocks.Count);
        Assert.All(userBlocks, b => Assert.Equal(BlockType.user_message, b.Type));
    }

    // ── Payload round-trip: every column MapFromBlock maps must reach the insert ──

    [Fact]
    public async Task AppendBlocks_KeepsToolResult()
    {
        const string agentId = "test-agent-tool-result";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        var block = MemoryBlock.ToolCall("bash", """{"command":"ls"}""");
        block.ToolResult = "file1\nfile2";

        await repo.AppendBlocksAsync(agentId, [block]);

        var loaded = Assert.Single(await repo.LoadBlocksAsync(agentId));
        Assert.Equal("file1\nfile2", loaded.ToolResult);
    }

    [Fact]
    public async Task ReplaceBlocksSince_KeepsToolResult()
    {
        const string agentId = "test-agent-tool-result-replace";
        using var sessionRepo = CreateSessionRepo();
        await sessionRepo.EnsureSessionAsync(agentId);

        using var repo = CreateBlockRepo();
        await repo.AppendBlocksAsync(agentId, [
            MemoryBlock.AgentData("agent", agentId),
            MemoryBlock.UserMessage("question"),
        ]);

        // Compaction re-inserts preserved turns through the same insert statement.
        var preserved = MemoryBlock.ToolCall("bash", """{"command":"ls"}""");
        preserved.ToolResult = "probe-ok";

        await repo.ReplaceBlocksSinceAsync(agentId, fromNumber: 2, [preserved], nextNumber: 3);

        var loaded = await repo.LoadBlocksAsync(agentId);
        var tool = Assert.Single(loaded, b => b.Type == BlockType.tool);
        Assert.Equal("probe-ok", tool.ToolResult);
    }
}