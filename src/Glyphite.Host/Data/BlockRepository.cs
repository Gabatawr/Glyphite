using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Glyphite.Host.Data;

public class BlockRepository : RepositoryBase, IBlockStore
{
    public BlockRepository(string connectionString) : base(connectionString)
    {
        Initialize();
    }

    private void Initialize()
    {
        _conn.Execute("""
            CREATE TABLE IF NOT EXISTS blocks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id TEXT NOT NULL REFERENCES sessions(id),
                number REAL NOT NULL,
                type TEXT NOT NULL,
                created_at TEXT NOT NULL,
                content TEXT NOT NULL,
                tool_name TEXT,
                data TEXT,
                model TEXT,
                tool_result TEXT,
                updated_at TEXT,
                parent_number REAL,
                is_deleted INTEGER NOT NULL DEFAULT 0
            );
            """);
        _conn.Execute("CREATE INDEX IF NOT EXISTS idx_blocks_agent_deleted ON blocks(agent_id, is_deleted)");
    }

    // ── Shared SQL ──

    private const string SqlInsertBlock = """
        INSERT INTO blocks (agent_id, number, type, created_at, updated_at, content, tool_name, data, model, parent_number, is_deleted)
        VALUES (@sid, @Number, @Type, @CreatedAt, @UpdatedAt, @Content, @ToolName, @Data, @Model, @parent_number, 0)
        """;

    private const string SqlSoftDeleteBlock = "UPDATE blocks SET is_deleted = 1 WHERE agent_id = @sid AND number = @num";

    private const string SqlRecoverBlock = "UPDATE blocks SET is_deleted = 0 WHERE agent_id = @sid AND number = @num AND is_deleted = 1";

    // ── Mapping ──

    private static MemoryBlock MapToBlock(BlockEntity e)
    {
        var type = Enum.TryParse<BlockType>(e.Type, ignoreCase: true, out var t) ? t : BlockType.system_info;
        return new MemoryBlock
        {
            Number = e.Number,
            Type = type,
            CreatedAt = DateTime.Parse(e.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
            UpdatedAt = e.UpdatedAt is not null
                ? DateTime.Parse(e.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : null,
            Content = e.Content,
            ToolName = e.ToolName,
            Data = e.Data is not null ? JsonSerializer.Deserialize<Dictionary<string, object>>(e.Data) : null,
            Model = e.Model,
            ParentNumber = e.ParentNumber,
            ToolResult = e.ToolResult
        };
    }

    private static object MapFromBlock(string agentId, MemoryBlock b)
    {
        return new
        {
            sid = agentId,
            b.Number,
            Type = b.Type.ToString(),
            CreatedAt = b.CreatedAt.ToString("O"),
            UpdatedAt = b.UpdatedAt?.ToString("O"),
            b.Content,
            b.ToolName,
            Data = b.Data is not null ? JsonSerializer.Serialize(b.Data) : null,
            b.Model,
            parent_number = b.ParentNumber,
            tool_result = b.ToolResult
        };
    }

    // ── Load ──

    private async Task<List<MemoryBlock>> LoadBlocksCoreAsync(string agentId)
    {
        var entities = await _conn.QueryAsync<BlockEntity>(
            "SELECT * FROM blocks WHERE agent_id = @sid AND is_deleted = 0 ORDER BY number",
            new { sid = agentId });
        return entities.Select(MapToBlock).ToList();
    }

    public async Task<List<MemoryBlock>> LoadBlocksAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        var entities = await conn.QueryAsync<BlockEntity>(
            "SELECT * FROM blocks WHERE agent_id = @sid AND is_deleted = 0 ORDER BY number",
            new { sid = agentId });
        return entities.Select(MapToBlock).ToList();
    }

    public async Task<MemoryBlock?> GetBlockAsync(string agentId, double number, bool includeDeleted = false)
    {
        using var conn = CreateReadConnection();
        var sql = includeDeleted
            ? "SELECT * FROM blocks WHERE agent_id = @sid AND number = @num"
            : "SELECT * FROM blocks WHERE agent_id = @sid AND number = @num AND is_deleted = 0";
        var entity = await conn.QueryFirstOrDefaultAsync<BlockEntity>(sql,
            new { sid = agentId, num = number });
        return entity is not null ? MapToBlock(entity) : null;
    }

    public async Task<List<MemoryBlock>> LoadBlocksByTypeAsync(string agentId, BlockType? type, int? limit, bool desc)
    {
        using var conn = CreateReadConnection();
        var sql = "SELECT * FROM blocks WHERE agent_id = @sid AND is_deleted = 0";
        if (type is not null)
            sql += " AND type = @type";
        sql += desc ? " ORDER BY number DESC" : " ORDER BY number";
        if (limit is not null)
            sql += " LIMIT @limit";
        return (await conn.QueryAsync<BlockEntity>(sql, new { sid = agentId, type = type?.ToString(), limit }))
                     .Select(MapToBlock).ToList();
    }

    // ── Write ──

    public async Task AppendBlocksAsync(string agentId, List<MemoryBlock> newBlocks, double nextNumber)
    {
        await WithLockAsync(async () =>
        {
            await using var tx = await _conn.BeginTransactionAsync();
            foreach (var block in newBlocks)
            {
                block.UpdatedAt = DateTime.UtcNow;
                await _conn.ExecuteAsync(SqlInsertBlock, MapFromBlock(agentId, block));
            }
            await _conn.ExecuteAsync(
                "UPDATE sessions SET next_number = @Next WHERE id = @Id",
                new { Id = agentId, Next = nextNumber });
            await tx.CommitAsync();
        });
    }

    public async Task UpdateBlockAsync(string agentId, double number, string? content = null, Dictionary<string, object>? data = null, string? model = null)
    {
        await WithLockAsync(async () =>
        {
            var dataJson = data is not null ? JsonSerializer.Serialize(data) : null;
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                UPDATE blocks SET
                    content = COALESCE(@Content, content),
                    data = COALESCE(@Data, data),
                    model = COALESCE(@Model, model),
                    updated_at = @Now
                WHERE agent_id = @sid AND number = @num
                """, new { sid = agentId, num = number, Content = content, Data = dataJson, Model = model, Now = now });
        });
    }

    public async Task UpdateBlockDataAsync(string agentId, double number, Dictionary<string, object>? data)
    {
        await WithLockAsync(async () =>
        {
            var json = data is not null ? JsonSerializer.Serialize(data) : null;
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync(
                "UPDATE blocks SET data = @Data, updated_at = @Now WHERE agent_id = @sid AND number = @num",
                new { sid = agentId, num = number, Data = json, Now = now });
        });
    }

    public async Task UpdateBlockToolResultAsync(string agentId, double number, string? toolResult)
    {
        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                UPDATE blocks SET
                    tool_result = @ToolResult,
                    updated_at = @Now
                WHERE agent_id = @sid AND number = @num
                """, new { sid = agentId, num = number, ToolResult = toolResult, Now = now });
        });
    }

    public async Task UpdateBlockContentAsync(string agentId, double number, string content)
    {
        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                UPDATE blocks SET content = @Content, updated_at = @Now
                WHERE agent_id = @sid AND number = @num
                """, new { sid = agentId, num = number, Content = content, Now = now });
        });
    }

    // ── Peek blocks ──

    public async Task<int> RemovePeekBlocksAsync(string agentId, bool includeReasoning = true)
    {
        return await WithLockAsync(async () =>
        {
            if (!includeReasoning) return 0;
            var sql = """
                UPDATE blocks SET is_deleted = 1
                WHERE agent_id = @sid AND is_deleted = 0
                  AND data IS NOT NULL AND json_extract(data, '$.peek') = 1
                  AND type = 'agent_reasoning'
                """;
            return await _conn.ExecuteAsync(sql, new { sid = agentId });
        });
    }

    public async Task<int> ClearPeekMarkersAsync(string agentId, bool includeReasoning = true)
    {
        return await WithLockAsync(async () =>
        {
            var typeFilter = includeReasoning ? "" : " AND type != 'agent_reasoning'";
            var sql = $"""
                UPDATE blocks SET
                    data = json_remove(data, '$.peek'),
                    tool_result = NULL
                WHERE agent_id = @sid AND is_deleted = 0
                  AND data IS NOT NULL AND json_extract(data, '$.peek') = 1{typeFilter}
                """;
            return await _conn.ExecuteAsync(sql, new { sid = agentId });
        });
    }

    public async Task<Dictionary<string, int>> GetPeekBlockStatsAsync(string agentId, bool includeReasoning = true)
    {
        using var conn = CreateReadConnection();
        var typeFilter = includeReasoning ? "" : " AND type != 'agent_reasoning'";
        var sql = $"""
            SELECT type, COUNT(*) as count
            FROM blocks
            WHERE agent_id = @sid AND is_deleted = 0
              AND data IS NOT NULL AND json_extract(data, '$.peek') = 1{typeFilter}
            GROUP BY type
            ORDER BY count DESC
            """;
        var rows = await conn.QueryAsync<(string type, int count)>(sql, new { sid = agentId });
        return rows.ToDictionary(r => r.type, r => r.count);
    }

    // ── Delete / Recover / Replace ──

    public async Task<int> RemoveBlocksAsync(string agentId, Predicate<MemoryBlock> match)
    {
        return await WithLockAsync(async () =>
        {
            var blocks = await LoadBlocksCoreAsync(agentId);
            var toRemove = blocks.Where(b => match(b)).Select(b => b.Number).ToArray();
            if (toRemove.Length == 0) return 0;

            await using var tx = await _conn.BeginTransactionAsync();
            var removed = 0;
            foreach (var num in toRemove)
                removed += await _conn.ExecuteAsync(
                    SqlSoftDeleteBlock,
                    new { sid = agentId, num });
            await tx.CommitAsync();
            return removed;
        });
    }

    public async Task<(int Removed, List<double> Protected)> DeleteBlocksAsync(string agentId, double[] numbers, HashSet<BlockType>? protectedTypes = null, bool cascade = true)
    {
        return await WithLockAsync(async () =>
        {
            var all = await LoadBlocksCoreAsync(agentId);
            protectedTypes ??= new HashSet<BlockType>
            {
                BlockType.agent_data
            };

            var protectedNums = new List<double>();
            var toRemove = new HashSet<double>();

            void CollectCascade(double num)
            {
                if (toRemove.Contains(num)) return;
                var block = all.FirstOrDefault(b => b.Number == num);
                if (block is null) return;
                if (protectedTypes.Contains(block.Type)) return;
                toRemove.Add(num);

                if (cascade && block.Data?.TryGetValue("parentNumber", out var pnRaw) == true)
                {
                    double? pn = pnRaw is JsonElement je && je.ValueKind == JsonValueKind.Number
                        ? je.GetDouble()
                        : pnRaw is double d ? d : null;
                    if (pn.HasValue)
                        CollectCascade(pn.Value);
                }
            }

            foreach (var num in numbers)
            {
                var block = all.FirstOrDefault(b => b.Number == num);
                if (block is null) continue;
                if (protectedTypes.Contains(block.Type))
                {
                    protectedNums.Add(num);
                    continue;
                }
                CollectCascade(num);
            }

            var removed = 0;
            if (toRemove.Count > 0)
            {
                await using var tx = await _conn.BeginTransactionAsync();
                foreach (var num in toRemove)
                    removed += await _conn.ExecuteAsync(
                    SqlSoftDeleteBlock,
                        new { sid = agentId, num });
                await tx.CommitAsync();
            }

            return (removed, protectedNums);
        });
    }

    public async Task<int> RecoverBlocksAsync(string agentId, double[] numbers, bool cascade = false)
    {
        return await WithLockAsync(async () =>
        {
            var toRecover = new HashSet<double>(numbers);

            if (cascade)
            {
                var all = await _conn.QueryAsync<BlockEntity>(
                    "SELECT * FROM blocks WHERE agent_id = @sid ORDER BY number",
                    new { sid = agentId });
                var blockIndex = all.ToDictionary(e => e.Number, MapToBlock);

                var queue = new Queue<double>(numbers);
                while (queue.Count > 0)
                {
                    var num = queue.Dequeue();
                    if (!blockIndex.TryGetValue(num, out var block)) continue;
                    if (block.Data?.TryGetValue("parentNumber", out var pnRaw) == true)
                    {
                        double? pn = pnRaw is JsonElement je && je.ValueKind == JsonValueKind.Number
                            ? je.GetDouble()
                            : pnRaw is double d ? d : null;
                        if (pn.HasValue && toRecover.Add(pn.Value))
                            queue.Enqueue(pn.Value);
                    }
                }
            }

            await using var tx = await _conn.BeginTransactionAsync();
            var removed = 0;
            foreach (var num in toRecover)
                removed += await _conn.ExecuteAsync(
                    SqlRecoverBlock,
                    new { sid = agentId, num });
            await tx.CommitAsync();
            return removed;
        });
    }

    public async Task<int> DeleteBlocksByFilterAsync(string agentId, string[]? types, TimeSpan? recent, HashSet<BlockType>? protectedTypes = null)
    {
        return await WithLockAsync(async () =>
        {
            var all = await LoadBlocksCoreAsync(agentId);
            protectedTypes ??= new HashSet<BlockType>
            {
                BlockType.agent_data
            };

            var typeSet = types is not null ? new HashSet<string>(types, StringComparer.OrdinalIgnoreCase) : null;
            var cutoff = recent.HasValue ? DateTime.UtcNow - recent.Value : (DateTime?)null;

            var toRemove = new List<double>();
            foreach (var block in all)
            {
                if (protectedTypes.Contains(block.Type))
                    continue;

                if (typeSet is not null && !typeSet.Contains(block.Type.ToString()))
                    continue;

                if (cutoff.HasValue && block.CreatedAt < cutoff.Value)
                    continue;

                toRemove.Add(block.Number);
            }

            if (toRemove.Count == 0) return 0;

            await using var tx = await _conn.BeginTransactionAsync();
            var removed = 0;
            foreach (var num in toRemove)
                removed += await _conn.ExecuteAsync(
                    SqlSoftDeleteBlock,
                    new { sid = agentId, num });
            await tx.CommitAsync();
            return removed;
        });
    }

    public async Task ClearAgentBlocksAsync(string agentId)
    {
        await WithLockAsync(async () =>
        {
            await using var tx = await _conn.BeginTransactionAsync();
            await _conn.ExecuteAsync("DELETE FROM blocks WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("UPDATE sessions SET next_number = 1 WHERE id = @id", new { id = agentId });
            await tx.CommitAsync();
        });
    }

    public async Task DeleteBlocksSinceAsync(string agentId, double fromNumber)
    {
        await WithLockAsync(async () =>
        {
            await _conn.ExecuteAsync(
                "DELETE FROM blocks WHERE agent_id = @sid AND number >= @num",
                new { sid = agentId, num = fromNumber });
        });
    }

    public async Task ReplaceBlocksSinceAsync(string agentId, double fromNumber, List<MemoryBlock> newBlocks, double nextNumber, HashSet<double>? softDeleteNums = null)
    {
        await WithLockAsync(async () =>
        {
            await using var tx = await _conn.BeginTransactionAsync();

            // 1. Soft-delete individual blocks (old zone unprotected blocks)
            if (softDeleteNums is not null && softDeleteNums.Count > 0)
            {
                foreach (var num in softDeleteNums)
                    await _conn.ExecuteAsync(
                    SqlSoftDeleteBlock,
                        new { sid = agentId, num });
            }

            // 2. Hard-delete everything from the cutoff point
            await _conn.ExecuteAsync(
                "DELETE FROM blocks WHERE agent_id = @sid AND number >= @num",
                new { sid = agentId, num = fromNumber });

            // 3. Insert new blocks (summaries + preserved newest zones)
            foreach (var block in newBlocks)
            {
                block.UpdatedAt = DateTime.UtcNow;
                await _conn.ExecuteAsync(SqlInsertBlock, MapFromBlock(agentId, block));
            }

            // 4. Update next_number
            await _conn.ExecuteAsync(
                "UPDATE sessions SET next_number = @Next WHERE id = @Id",
                new { Id = agentId, Next = nextNumber });

            await tx.CommitAsync();
        });
    }

    // ── Stats ──

    public async Task<int> GetBlockCountAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM blocks WHERE agent_id = @sid AND is_deleted = 0",
            new { sid = agentId });
    }

    public async Task<Dictionary<string, int>> GetBlockTypeStatsAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        var rows = await conn.QueryAsync<(string Type, int Count)>(
            @"SELECT type, COUNT(*) FROM blocks 
              WHERE agent_id = @sid AND is_deleted = 0 
              GROUP BY type ORDER BY COUNT(*) DESC",
            new { sid = agentId });

        var result = new Dictionary<string, int>();
        foreach (var r in rows)
            result[r.Type] = r.Count;
        return result;
    }
}
