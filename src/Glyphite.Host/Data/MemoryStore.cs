using System.Text.Json;
using Glyphite.Abstractions.Models;
using Glyphite.Abstractions.Interfaces;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Glyphite.Host.Data;

public partial class MemoryStore : IMemoryStore
{
    private readonly SqliteConnection _conn;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public MemoryStore(string connectionString)
    {
        _connectionString = connectionString;
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        Initialize();
    }

    /// <summary>Create a short-lived read-only connection (pooled by Microsoft.Data.Sqlite).</summary>
    private SqliteConnection CreateReadConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    static MemoryStore()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public static MemoryStore CreateForApp(string dataDir, string? dbFileName = null)
    {
        Directory.CreateDirectory(dataDir);
        dbFileName ??= "Glyphite.db";
        return new MemoryStore($"Data Source={Path.Combine(dataDir, dbFileName)}");
    }

    public static MemoryStore CreateInMemory()
        => new("Data Source=:memory:");

    public void Dispose()
    {
        _writeLock.Dispose();
        _conn?.Close();
        _conn?.Dispose();
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
            CREATE TABLE IF NOT EXISTS config (
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                scope TEXT NOT NULL DEFAULT 'global',
                agent_id TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (key, scope, agent_id)
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
            """);
        try { _conn.Execute("ALTER TABLE session_usage ADD COLUMN last_request_hit INTEGER NOT NULL DEFAULT 0"); } catch { }
        try { _conn.Execute("ALTER TABLE session_usage ADD COLUMN last_request_miss INTEGER NOT NULL DEFAULT 0"); } catch { }
        try { _conn.Execute("ALTER TABLE session_usage ADD COLUMN model TEXT"); } catch { }
        _conn.Execute("CREATE INDEX IF NOT EXISTS idx_session_usage_agent ON session_usage(agent_id)");
        _conn.Execute("CREATE INDEX IF NOT EXISTS idx_blocks_agent_deleted ON blocks(agent_id, is_deleted)");
    }

    // ── Session ──

    public async Task EnsureSessionAsync(string id, string? homePath = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                INSERT OR IGNORE INTO sessions (id, next_number, created_at, home_path)
                VALUES (@Id, 1, @Now, @Path)
                """, new { Id = id, Now = now, Path = homePath ?? "" });
        }
        finally { _writeLock.Release(); }
    }

    public async Task RecordLaunchAsync(string agentId, string path)
    {
        await _writeLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync("""
                INSERT INTO agent_launches (agent_id, path, last_active_at)
                VALUES (@AgentId, @Path, @Now)
                ON CONFLICT(agent_id, path) DO UPDATE SET last_active_at = @Now
                """, new { AgentId = agentId, Path = path, Now = now });
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<(string path, string lastActive)>> GetLaunchesAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        var rows = await conn.QueryAsync<(string path, string lastActive)>(
            "SELECT path, last_active_at FROM agent_launches WHERE agent_id = @aid ORDER BY last_active_at DESC",
            new { aid = agentId });
        return rows.ToList();
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

    public async Task<string?> GetLastLaunchPathAsync(string agentId)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT path FROM agent_launches WHERE agent_id = @aid ORDER BY last_active_at DESC LIMIT 1",
            new { aid = agentId });
    }

    public async Task<string?> GetAgentModelAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT current_model FROM sessions WHERE id = @id", new { id });
    }

    public async Task<bool> AgentExistsAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM sessions WHERE id = @id", new { id }) > 0;
    }

    public async Task<bool> SetAgentModelAsync(string id, string model)
    {
        await _writeLock.WaitAsync();
        try
        {
            var rows = await _conn.ExecuteAsync(
                "UPDATE sessions SET current_model = @Model WHERE id = @Id",
                new { Id = id, Model = model });
            return rows > 0;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<double> GetNextNumberAsync(string id)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<double>(
            "SELECT next_number FROM sessions WHERE id = @id", new { id });
    }

    public async Task SetNextNumberAsync(string id, double next)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _conn.ExecuteAsync(
                "UPDATE sessions SET next_number = @Next WHERE id = @Id",
                new { Id = id, Next = next });
        }
        finally { _writeLock.Release(); }
    }

    // ── Delete ──

    public async Task DeleteSessionAsync(string agentId)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var tx = await _conn.BeginTransactionAsync();
            await _conn.ExecuteAsync("DELETE FROM session_usage WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM blocks WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM config WHERE scope = 'session' AND agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM agent_launches WHERE agent_id = @sid", new { sid = agentId });
            await _conn.ExecuteAsync("DELETE FROM sessions WHERE id = @sid", new { sid = agentId });
            await tx.CommitAsync();
        }
        finally { _writeLock.Release(); }
    }

    // ── Diagnostics ──

    public async Task<int> DirectExecuteAsync(string sql, object? param = null)
    {
        await _writeLock.WaitAsync();
        try { return await _conn.ExecuteAsync(sql, param); }
        finally { _writeLock.Release(); }
    }

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

    public async Task<string?> GetLastActiveAgentAsync(string cwd)
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT agent_id FROM agent_launches WHERE path = @cwd ORDER BY last_active_at DESC LIMIT 1",
            new { cwd });
    }

    // ── Usage tracking ──

    public async Task RecordUsageAsync(string agentId, long cacheHit, long cacheMiss, long output, long lastRequestHit = 0, long lastRequestMiss = 0, string? model = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _conn.ExecuteAsync("""
                INSERT INTO session_usage (agent_id, cache_hit, cache_miss, output_tokens, model, last_request_hit, last_request_miss, created_at)
                VALUES (@Id, @Hit, @Miss, @Output, @Model, @LastHit, @LastMiss, datetime('now'))
                """, new { Id = agentId, Hit = cacheHit, Miss = cacheMiss, Output = output, Model = model, LastHit = lastRequestHit, LastMiss = lastRequestMiss });
        }
        finally { _writeLock.Release(); }
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
        await _writeLock.WaitAsync();
        try
        {
            await _conn.ExecuteAsync("DELETE FROM session_usage WHERE agent_id = @sid", new { sid = agentId });
        }
        finally { _writeLock.Release(); }
    }


    private sealed record BlockEntity
    {
        public int Id { get; set; }
        public double Number { get; set; }
        public string Type { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string? UpdatedAt { get; set; }
        public string Content { get; set; } = "";
        public string? ToolName { get; set; }
        public string? Data { get; set; }
        public string? Model { get; set; }
        public double? ParentNumber { get; set; }
        public string? ToolResult { get; set; }
    }
}
