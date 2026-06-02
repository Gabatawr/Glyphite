using System.Text.Json;
using Glyphite.Abstractions.Models;
using Dapper;

namespace Glyphite.Host.Data;

public partial class MemoryStore
{
    private async Task<List<MemoryBlock>> LoadBlocksCoreAsync(string agentId)
    {
        var entities = await _conn.QueryAsync<BlockEntity>(
            "SELECT * FROM blocks WHERE agent_id = @sid AND is_deleted = 0 ORDER BY number",
            new { sid = agentId });
        return entities.Select(MapToBlock).ToList();
    }

    public async Task<List<MemoryBlock>> LoadBlocksAsync(string agentId)
    {
        await _lock.WaitAsync();
        try { return await LoadBlocksCoreAsync(agentId); }
        finally { _lock.Release(); }
    }

    public async Task<MemoryBlock?> GetBlockAsync(string agentId, double number, bool includeDeleted = false)
    {
        await _lock.WaitAsync();
        try
        {
            var sql = includeDeleted
                ? "SELECT * FROM blocks WHERE agent_id = @sid AND number = @num"
                : "SELECT * FROM blocks WHERE agent_id = @sid AND number = @num AND is_deleted = 0";
            var entity = await _conn.QueryFirstOrDefaultAsync<BlockEntity>(sql,
                new { sid = agentId, num = number });
            return entity is not null ? MapToBlock(entity) : null;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<MemoryBlock>> LoadBlocksByTypeAsync(string agentId, BlockType? type, int? limit, bool desc)
    {
        await _lock.WaitAsync();
        try
        {
            var sql = "SELECT * FROM blocks WHERE agent_id = @sid AND is_deleted = 0";
            if (type is not null)
                sql += " AND type = @type";
            sql += desc ? " ORDER BY number DESC" : " ORDER BY number";
            if (limit is not null)
                sql += " LIMIT @limit";
            return (await _conn.QueryAsync<BlockEntity>(sql, new { sid = agentId, type = type?.ToString(), limit }))
                         .Select(MapToBlock).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task AppendBlocksAsync(string agentId, List<MemoryBlock> newBlocks, double nextNumber)
    {
        await _lock.WaitAsync();
        try
        {
            await using var tx = await _conn.BeginTransactionAsync();
            foreach (var block in newBlocks)
            {
                block.UpdatedAt = DateTime.UtcNow;
                await _conn.ExecuteAsync("""
                    INSERT INTO blocks (agent_id, number, type, created_at, updated_at, content, tool_name, data, model, parent_number, is_deleted)
                    VALUES (@sid, @Number, @Type, @CreatedAt, @UpdatedAt, @Content, @ToolName, @Data, @Model, @parent_number, 0)
                    """, MapFromBlock(agentId, block));
            }
            await _conn.ExecuteAsync(
                "UPDATE sessions SET next_number = @Next WHERE id = @Id",
                new { Id = agentId, Next = nextNumber });
            await tx.CommitAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RemovePeekBlocksAsync(string agentId)
    {
        await _lock.WaitAsync();
        try
        {
            return await _conn.ExecuteAsync("""
                UPDATE blocks SET is_deleted = 1
                WHERE agent_id = @sid AND is_deleted = 0
                  AND data IS NOT NULL AND json_extract(data, '$.peek') = 1
                """, new { sid = agentId });
        }
        finally { _lock.Release(); }
    }

    public async Task<Dictionary<string, int>> GetPeekBlockStatsAsync(string agentId)
    {
        await _lock.WaitAsync();
        try
        {
            var rows = await _conn.QueryAsync<(string type, int count)>("""
                SELECT type, COUNT(*) as count
                FROM blocks
                WHERE agent_id = @sid AND is_deleted = 0
                  AND data IS NOT NULL AND json_extract(data, '$.peek') = 1
                GROUP BY type
                ORDER BY count DESC
                """, new { sid = agentId });
            return rows.ToDictionary(r => r.type, r => r.count);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RemoveBlocksAsync(string agentId, Predicate<MemoryBlock> match)
    {
        await _lock.WaitAsync();
        try
        {
            var blocks = await LoadBlocksCoreAsync(agentId);
            var toRemove = blocks.Where(b => match(b)).Select(b => b.Number).ToArray();
            if (toRemove.Length == 0) return 0;

            await using var tx = await _conn.BeginTransactionAsync();
            var removed = 0;
            foreach (var num in toRemove)
                removed += await _conn.ExecuteAsync(
                    "UPDATE blocks SET is_deleted = 1 WHERE agent_id = @sid AND number = @num",
                    new { sid = agentId, num });
            await tx.CommitAsync();
            return removed;
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateBlockDataAsync(string agentId, double number, Dictionary<string, object>? data)
    {
        await _lock.WaitAsync();
        try
        {
            var json = data is not null ? JsonSerializer.Serialize(data) : null;
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync(
                "UPDATE blocks SET data = @Data, updated_at = @Now WHERE agent_id = @sid AND number = @num",
                new { sid = agentId, num = number, Data = json, Now = now });
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateBlockAsync(string agentId, double number, string? content = null, Dictionary<string, object>? data = null, string? model = null)
    {
        await _lock.WaitAsync();
        try
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
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateBlockToolResultAsync(string agentId, double number, string? toolResult)
    {
        await _lock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                UPDATE blocks SET
                    tool_result = @ToolResult,
                    updated_at = @Now
                WHERE agent_id = @sid AND number = @num
                """, new { sid = agentId, num = number, ToolResult = toolResult, Now = now });
        }
        finally { _lock.Release(); }
    }

    public async Task<(int Removed, List<double> Protected)> DeleteBlocksAsync(string agentId, double[] numbers, HashSet<BlockType>? protectedTypes = null)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await LoadBlocksCoreAsync(agentId);
            protectedTypes ??= new HashSet<BlockType>
            {
                BlockType.agent_data,
                BlockType.user_message, BlockType.agent_message
            };

            var protectedNums = new List<double>();
            var toRemove = new List<double>();

            foreach (var num in numbers)
            {
                var block = all.FirstOrDefault(b => b.Number == num);
                if (block is null) continue;
                if (protectedTypes.Contains(block.Type))
                {
                    protectedNums.Add(num);
                    continue;
                }
                toRemove.Add(num);
            }

            var removed = 0;
            if (toRemove.Count > 0)
            {
                await using var tx = await _conn.BeginTransactionAsync();
                foreach (var num in toRemove)
                    removed += await _conn.ExecuteAsync(
                        "UPDATE blocks SET is_deleted = 1 WHERE agent_id = @sid AND number = @num",
                        new { sid = agentId, num });
                await tx.CommitAsync();
            }

            return (removed, protectedNums);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RecoverBlocksAsync(string agentId, double[] numbers)
    {
        await _lock.WaitAsync();
        try
        {
            await using var tx = await _conn.BeginTransactionAsync();
            var removed = 0;
            foreach (var num in numbers)
                removed += await _conn.ExecuteAsync(
                    "UPDATE blocks SET is_deleted = 0 WHERE agent_id = @sid AND number = @num AND is_deleted = 1",
                    new { sid = agentId, num });
            await tx.CommitAsync();
            return removed;
        }
        finally { _lock.Release(); }
    }

    public async Task ForkSessionAsync(string sourceId, string targetId, string cwd)
    {
        await _lock.WaitAsync();
        try
        {
            await using var tx = await _conn.BeginTransactionAsync();

            // Copy session (model + home_path)
            var srcInfo = await _conn.QueryFirstOrDefaultAsync<(string? model, string? homePath)>(
                "SELECT current_model, home_path FROM sessions WHERE id = @id", new { id = sourceId });
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                INSERT OR IGNORE INTO sessions (id, current_model, next_number, created_at, home_path)
                VALUES (@Id, @Model, 1, @Now, @Path)
                """, new { Id = targetId, Model = srcInfo.model, Now = now, Path = cwd });

            // Copy blocks (with new agent_id, reset numbers)
            var blocks = await _conn.QueryAsync<BlockEntity>(
                "SELECT * FROM blocks WHERE agent_id = @sid AND is_deleted = 0 ORDER BY number",
                new { sid = sourceId });

            double num = 1;
            foreach (var b in blocks)
            {
                await _conn.ExecuteAsync("""
                    INSERT INTO blocks (agent_id, number, type, created_at, updated_at, content, tool_name, data, model, parent_number, is_deleted)
                    VALUES (@sid, @Number, @Type, @CreatedAt, @UpdatedAt, @Content, @ToolName, @Data, @Model, @parent_number, 0)
                    """, new
                {
                    sid = targetId,
                    Number = num++,
                    b.Type,
                    CreatedAt = b.CreatedAt,
                    UpdatedAt = b.UpdatedAt,
                    b.Content,
                    b.ToolName,
                    b.Data,
                    b.Model,
                    parent_number = b.ParentNumber
                });
            }

            // Copy session-scoped config
            var configs = await _conn.QueryAsync<ConfigRow>(
                "SELECT key, value, scope, agent_id, updated_at FROM config WHERE scope = 'session' AND agent_id = @sid",
                new { sid = sourceId });
            foreach (var cfg in configs)
            {
                await _conn.ExecuteAsync("""
                    INSERT INTO config (key, value, scope, agent_id, updated_at)
                    VALUES (@key, @value, 'session', @sid, @now)
                    ON CONFLICT(key, scope, agent_id) DO UPDATE SET value = @value, updated_at = @now
                    """, new { key = cfg.Key, value = cfg.Value, sid = targetId, now });
            }

            // Record first launch for the new agent
            await _conn.ExecuteAsync("""
                INSERT INTO agent_launches (agent_id, path, last_active_at)
                VALUES (@AgentId, @Path, @Now)
                ON CONFLICT(agent_id, path) DO UPDATE SET last_active_at = @Now
                """, new { AgentId = targetId, Path = cwd, Now = now });

            await tx.CommitAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task ClearAgentBlocksAsync(string agentId)
    {
        await _lock.WaitAsync();
        try
        {
            await using var tx = await _conn.BeginTransactionAsync();
            await _conn.ExecuteAsync("DELETE FROM blocks WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("UPDATE sessions SET next_number = 1 WHERE id = @id", new { id = agentId });
            await tx.CommitAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<string>> ListAgentsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return (await _conn.QueryAsync<string>(
                "SELECT id FROM sessions ORDER BY id")).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> GetBlockCountAsync(string agentId)
    {
        await _lock.WaitAsync();
        try
        {
            return await _conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM blocks WHERE agent_id = @sid AND is_deleted = 0",
                new { sid = agentId });
        }
        finally { _lock.Release(); }
    }

    public async Task<Dictionary<string, int>> GetBlockTypeStatsAsync(string agentId)
    {
        await _lock.WaitAsync();
        try
        {
            var rows = await _conn.QueryAsync<(string Type, int Count)>(
                @"SELECT type, COUNT(*) FROM blocks 
                  WHERE agent_id = @sid AND is_deleted = 0 
                  GROUP BY type ORDER BY COUNT(*) DESC",
                new { sid = agentId });

            var result = new Dictionary<string, int>();
            foreach (var r in rows)
                result[r.Type] = r.Count;
            return result;
        }
        finally { _lock.Release(); }
    }

    public async Task<MemoryBlock?> FindFileBlockByPathAsync(string agentId, string filePath)
    {
        await _lock.WaitAsync();
        try
        {
            var normalized = Path.GetFullPath(filePath);
            var all = await LoadBlocksCoreAsync(agentId);
            return all.FirstOrDefault(b =>
                b.Type == BlockType.file &&
                b.Data?.TryGetValue("path", out var p) == true &&
                p?.ToString() == normalized);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteFileBlocksByPathAsync(string agentId, string filePath)
    {
        await _lock.WaitAsync();
        try
        {
            var normalized = Path.GetFullPath(filePath);
            var all = await LoadBlocksCoreAsync(agentId);
            var toRemove = all.Where(b =>
                b.Type == BlockType.file &&
                b.Data?.TryGetValue("path", out var p) == true &&
                p?.ToString() == normalized)
                .Select(b => b.Number).ToList();

            if (toRemove.Count == 0) return;

            await using var tx = await _conn.BeginTransactionAsync();
            foreach (var num in toRemove)
                await _conn.ExecuteAsync(
                    "UPDATE blocks SET is_deleted = 1 WHERE agent_id = @sid AND number = @num",
                    new { sid = agentId, num });
            await tx.CommitAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteFileBlocksWithToolsByPathAsync(string agentId, string filePath)
    {
        await _lock.WaitAsync();
        try
        {
            var normalized = Path.GetFullPath(filePath);
            var all = await LoadBlocksCoreAsync(agentId);
            var allList = all.OrderBy(b => b.Number).ToList();

            var fileNums = allList.Where(b =>
                b.Type == BlockType.file &&
                b.Data?.TryGetValue("path", out var p) == true &&
                p?.ToString() == normalized)
                .Select(b => b.Number).ToHashSet();

            if (fileNums.Count == 0) return;

            var toRemove = new HashSet<double>(fileNums);

            // Also remove preceding write_file/read_file tool blocks
            foreach (var num in fileNums)
            {
                var prev = allList.LastOrDefault(b => b.Number < num && !fileNums.Contains(b.Number));
                if (prev is not null &&
                    prev.Type == BlockType.tool &&
                    (prev.ToolName == "write_file" || prev.ToolName == "read_file"))
                {
                    toRemove.Add(prev.Number);
                }
            }

            await using var tx = await _conn.BeginTransactionAsync();
            foreach (var num in toRemove)
                await _conn.ExecuteAsync(
                    "UPDATE blocks SET is_deleted = 1 WHERE agent_id = @sid AND number = @num",
                    new { sid = agentId, num });
            await tx.CommitAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> DeleteBlocksByFilterAsync(string agentId, string[]? types, TimeSpan? recent, HashSet<BlockType>? protectedTypes = null)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await LoadBlocksCoreAsync(agentId);
            protectedTypes ??= new HashSet<BlockType>
            {
                BlockType.agent_data,
                BlockType.user_message, BlockType.agent_message
            };

            var typeSet = types?.Select(t => t.ToLowerInvariant()).ToHashSet();
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
                    "UPDATE blocks SET is_deleted = 1 WHERE agent_id = @sid AND number = @num",
                    new { sid = agentId, num });
            await tx.CommitAsync();
            return removed;
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateBlockContentAsync(string agentId, double number, string content)
    {
        await _lock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                UPDATE blocks SET content = @Content, updated_at = @Now
                WHERE agent_id = @sid AND number = @num
                """, new { sid = agentId, num = number, Content = content, Now = now });
        }
        finally { _lock.Release(); }
    }
}