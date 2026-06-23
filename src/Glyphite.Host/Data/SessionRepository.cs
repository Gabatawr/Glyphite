using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Glyphite.Host.Data;

public class SessionRepository : RepositoryBase, IAgentStore
{
    public SessionRepository(string connectionString) : base(connectionString)
    {
        Initialize();
    }

    private void Initialize()
    {
        _conn.Execute("""
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                current_model TEXT,
                next_number REAL NOT NULL DEFAULT 1,
                created_at TEXT,
                home_path TEXT
            );
            CREATE TABLE IF NOT EXISTS agent_launches (
                agent_id TEXT NOT NULL REFERENCES sessions(id),
                path TEXT NOT NULL,
                last_active_at TEXT NOT NULL,
                PRIMARY KEY (agent_id, path)
            );
            CREATE TABLE IF NOT EXISTS session_usage (
                agent_id TEXT NOT NULL REFERENCES sessions(id),
                cache_hit INTEGER NOT NULL DEFAULT 0,
                cache_miss INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                model TEXT,
                last_request_hit INTEGER NOT NULL DEFAULT 0,
                last_request_miss INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pending_runs (
                agent_id TEXT PRIMARY KEY,
                mode TEXT NOT NULL,
                block_checkpoint REAL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL,
                applied_at TEXT NOT NULL
            );
            """);
        _conn.Execute("CREATE INDEX IF NOT EXISTS idx_session_usage_agent ON session_usage(agent_id)");
        ApplyMigrations();
    }

    private void ApplyMigrations()
    {
        var currentVersion = _conn.QueryFirstOrDefault<int>(
            "SELECT COALESCE(MAX(version), 0) FROM schema_version");

        if (currentVersion < 1)
        {
            if (!ColumnExists("session_usage", "last_request_hit"))
                _conn.Execute("ALTER TABLE session_usage ADD COLUMN last_request_hit INTEGER NOT NULL DEFAULT 0");
            if (!ColumnExists("session_usage", "last_request_miss"))
                _conn.Execute("ALTER TABLE session_usage ADD COLUMN last_request_miss INTEGER NOT NULL DEFAULT 0");
            if (!ColumnExists("session_usage", "model"))
                _conn.Execute("ALTER TABLE session_usage ADD COLUMN model TEXT");
            _conn.Execute("INSERT INTO schema_version (version, applied_at) VALUES (1, datetime('now'))");
        }
    }

    private bool ColumnExists(string table, string column)
    {
        var count = _conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM pragma_table_info(@table) WHERE name = @column",
            new { table, column });
        return count > 0;
    }

    // ── Session ──

    public async Task EnsureSessionAsync(string id, string? homePath = null)
    {
        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                INSERT OR IGNORE INTO sessions (id, next_number, created_at, home_path)
                VALUES (@Id, 1, @Now, @Path)
                """, new { Id = id, Now = now, Path = homePath ?? "" });
        });
    }

    public async Task<string?> GetAgentHomePathAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT home_path FROM sessions WHERE id = @id", new { id });
    }

    public async Task<string?> GetAgentCreatedAtAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT created_at FROM sessions WHERE id = @id", new { id });
    }

    public async Task<string?> GetAgentModelAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT current_model FROM sessions WHERE id = @id", new { id });
    }

    public async Task<bool> SetAgentModelAsync(string id, string model)
    {
        return await WithLockAsync(async () =>
        {
            var rows = await _conn.ExecuteAsync(
                "UPDATE sessions SET current_model = @Model WHERE id = @Id",
                new { Id = id, Model = model });
            return rows > 0;
        });
    }

    public async Task<bool> AgentExistsAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM sessions WHERE id = @id", new { id }) > 0;
    }

    public async Task<double> GetNextNumberAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<double>(
            "SELECT next_number FROM sessions WHERE id = @id", new { id });
    }

    public async Task SetNextNumberAsync(string id, double next)
    {
        await WithLockAsync(async () =>
        {
            await _conn.ExecuteAsync(
                "UPDATE sessions SET next_number = @Next WHERE id = @Id",
                new { Id = id, Next = next });
        });
    }

    public async Task DeleteSessionAsync(string agentId)
    {
        await WithLockAsync(async () =>
        {
            await using var tx = await _conn.BeginTransactionAsync();
            await _conn.ExecuteAsync("DELETE FROM session_usage WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM blocks WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM config WHERE scope = 'session' AND agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM agent_launches WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM sessions WHERE id = @sid", new { sid = agentId });
            await tx.CommitAsync();
        });
    }

    public async Task RecordLaunchAsync(string agentId, string path)
    {
        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                INSERT INTO agent_launches (agent_id, path, last_active_at)
                VALUES (@AgentId, @Path, @Now)
                ON CONFLICT(agent_id, path) DO UPDATE SET last_active_at = @Now
                """, new { AgentId = agentId, Path = path, Now = now });
        });
    }

    public async Task<List<(string path, string lastActive)>> GetLaunchesAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        var rows = await conn.QueryAsync<(string path, string lastActive)>(
            "SELECT path, last_active_at FROM agent_launches WHERE agent_id = @aid ORDER BY last_active_at DESC",
            new { aid = agentId });
        return rows.ToList();
    }

    public async Task<string?> GetLastLaunchPathAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT path FROM agent_launches WHERE agent_id = @aid ORDER BY last_active_at DESC LIMIT 1",
            new { aid = agentId });
    }

    public async Task<string?> GetLastActiveAgentAsync(string cwd)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT agent_id FROM agent_launches WHERE path = @cwd ORDER BY last_active_at DESC LIMIT 1",
            new { cwd });
    }

    public async Task<List<string>> ListAgentsAsync()
    {
        using var conn = CreateReadConnection();
        return (await conn.QueryAsync<string>(
            "SELECT id FROM sessions ORDER BY id")).ToList();
    }

    public async Task<string?> GetSessionIdByWorkingDirectoryAsync(string cwd)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT agent_id FROM config WHERE key = 'Session:WorkingDirectory' AND value = @cwd AND scope = 'session' ORDER BY updated_at DESC LIMIT 1",
            new { cwd });
    }

    public async Task ForkSessionAsync(string sourceId, string targetId, string cwd)
    {
        await WithLockAsync(async () =>
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
        });
    }

    // ── Usage tracking ──

    public async Task RecordUsageAsync(string agentId, long cacheHit, long cacheMiss, long output, long lastRequestHit = 0, long lastRequestMiss = 0, string? model = null)
    {
        await WithLockAsync(async () =>
        {
            await _conn.ExecuteAsync("""
                INSERT INTO session_usage (agent_id, cache_hit, cache_miss, output_tokens, model, last_request_hit, last_request_miss, created_at)
                VALUES (@Id, @Hit, @Miss, @Output, @Model, @LastHit, @LastMiss, datetime('now'))
                """, new { Id = agentId, Hit = cacheHit, Miss = cacheMiss, Output = output, Model = model, LastHit = lastRequestHit, LastMiss = lastRequestMiss });
        });
    }

    public async Task<(long Hit, long Miss, long Output)> GetUsageAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        var result = await conn.QueryFirstOrDefaultAsync<(long Hit, long Miss, long Output)>(
            "SELECT COALESCE(SUM(cache_hit), 0), COALESCE(SUM(cache_miss), 0), COALESCE(SUM(output_tokens), 0) FROM session_usage WHERE agent_id = @Id",
            new { Id = agentId });
        return result;
    }

    public async Task<List<(string Model, long Hit, long Miss, long Output)>> GetUsageByModelAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        var rows = await conn.QueryAsync<(string? Model, long Hit, long Miss, long Output)>(
            "SELECT COALESCE(model, ''), COALESCE(SUM(cache_hit), 0), COALESCE(SUM(cache_miss), 0), COALESCE(SUM(output_tokens), 0) FROM session_usage WHERE agent_id = @Id GROUP BY model",
            new { Id = agentId });
        return rows.Select(r => (r.Model ?? "", r.Hit, r.Miss, r.Output)).ToList();
    }

    public async Task<(long Hit, long Miss, long Output, long LastHit, long LastMiss)> GetLastUsageAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        var result = await conn.QueryFirstOrDefaultAsync<(long Hit, long Miss, long Output, long LastHit, long LastMiss)>(
            "SELECT cache_hit, cache_miss, output_tokens, last_request_hit, last_request_miss FROM session_usage WHERE agent_id = @Id ORDER BY rowid DESC LIMIT 1",
            new { Id = agentId });
        return result;
    }

    public async Task ClearUsageAsync(string agentId)
    {
        await WithLockAsync(async () =>
        {
            await _conn.ExecuteAsync("DELETE FROM session_usage WHERE agent_id = @sid", new { sid = agentId });
        });
    }

    // ── Pending runs (crash-safe tracking) ──

    public async Task SetPendingRunAsync(string agentId, string mode, double? blockCheckpoint = null)
    {
        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                INSERT INTO pending_runs (agent_id, mode, block_checkpoint, created_at)
                VALUES (@Id, @Mode, @Ck, @Now)
                ON CONFLICT(agent_id) DO UPDATE SET mode = @Mode, block_checkpoint = @Ck, created_at = @Now
                """, new { Id = agentId, Mode = mode, Ck = blockCheckpoint, Now = now });
        });
    }

    public async Task ClearPendingRunAsync(string agentId)
    {
        await WithLockAsync(async () =>
        {
            await _conn.ExecuteAsync("DELETE FROM pending_runs WHERE agent_id = @Id", new { Id = agentId });
        });
    }

    public async Task<List<(string AgentId, string Mode, double? BlockCheckpoint)>> GetPendingRunsAsync()
    {
        using var conn = CreateReadConnection();
        var rows = await conn.QueryAsync<(string AgentId, string Mode, double? BlockCheckpoint)>(
            "SELECT agent_id, mode, block_checkpoint FROM pending_runs");
        return rows.AsList();
    }
}