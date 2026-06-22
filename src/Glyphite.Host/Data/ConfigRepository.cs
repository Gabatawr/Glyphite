using Glyphite.Abstractions.Interfaces;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Glyphite.Host.Data;

public class ConfigRepository : RepositoryBase, IConfigStore
{
    public ConfigRepository(string connectionString) : base(connectionString)
    {
        Initialize();
    }

    private void Initialize()
    {
        _conn.Execute("""
            CREATE TABLE IF NOT EXISTS config (
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                scope TEXT NOT NULL DEFAULT 'global',
                agent_id TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (key, scope, agent_id)
            );
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL,
                applied_at TEXT NOT NULL
            );
            """);
    }

    // ── Config CRUD ──

    public async Task<string?> GetConfigAsync(string key, string scope = "global", string? agentId = null)
    {
        using var conn = CreateReadConnection();
        var sid = agentId ?? "";
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM config WHERE key = @key AND scope = @scope AND agent_id = @sid",
            new { key, scope, sid });
    }

    public async Task UpsertConfigAsync(string key, string value, string scope = "global", string? agentId = null)
    {
        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            var sid = agentId ?? "";
            await _conn.ExecuteAsync("""
                INSERT INTO config (key, value, scope, agent_id, updated_at)
                VALUES (@key, @value, @scope, @sid, @now)
                ON CONFLICT(key, scope, agent_id) DO UPDATE SET value = @value, updated_at = @now
                """, new { key, value, scope, sid, now });
        });
    }

    public async Task DeleteConfigAsync(string key, string scope = "global", string? agentId = null)
    {
        await WithLockAsync(async () =>
        {
            var sid = agentId ?? "";
            await _conn.ExecuteAsync(
                "DELETE FROM config WHERE key = @key AND scope = @scope AND agent_id = @sid",
                new { key, scope, sid });
        });
    }

    public async Task DeleteConfigByScopeAsync(string scope, string? agentId = null)
    {
        await WithLockAsync(async () =>
        {
            if (agentId is null)
                await _conn.ExecuteAsync(
                    "DELETE FROM config WHERE scope = @scope",
                    new { scope });
            else
                await _conn.ExecuteAsync(
                    "DELETE FROM config WHERE scope = @scope AND agent_id = @sid",
                    new { scope, sid = agentId });
        });
    }

    public async Task<Dictionary<string, string>> GetMergedConfigAsync(string? agentId = null)
    {
        using var conn = CreateReadConnection();
        var sql = agentId is not null ? """
            SELECT key, value FROM config WHERE scope = 'global'
            UNION ALL
            SELECT key, value FROM config WHERE scope = 'session' AND agent_id = @sid
            """ : "SELECT key, value FROM config WHERE scope = 'global'";
        var rows = await conn.QueryAsync<(string Key, string Value)>(sql, new { sid = agentId ?? "" });
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rows)
            merged[key] = value;
        return merged;
    }

    // ── Internal helpers ──

    internal async Task<List<ConfigRow>> GetAllConfigAsync(string scope = "global", string? agentId = null)
    {
        using var conn = CreateReadConnection();
        var sql = "SELECT key, value, scope, agent_id, updated_at FROM config WHERE scope = @scope";
        if (agentId is not null)
            sql += " AND agent_id = @sid";
        return (await conn.QueryAsync<ConfigRow>(sql, new { scope, sid = agentId ?? "" })).AsList();
    }

    internal async Task<bool> ConfigExistsAsync(string key, string scope = "global", string? agentId = null)
    {
        using var conn = CreateReadConnection();
        var sid = agentId ?? "";
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM config WHERE key = @key AND scope = @scope AND agent_id = @sid",
            new { key, scope, sid }) > 0;
    }

    internal async Task<int> GetConfigCountAsync()
    {
        using var conn = CreateReadConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM config");
    }

    internal async Task<string?> GetLastSessionIdAsync()
    {
        using var conn = CreateReadConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT agent_id FROM blocks ORDER BY created_at DESC LIMIT 1");
    }
}
