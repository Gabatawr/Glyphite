using Dapper;

namespace Glyphite.Host.Data;

public partial class MemoryStore
{
    public sealed record ConfigRow(string Key, string Value, string Scope, string AgentId, string UpdatedAt);

    public async Task<List<ConfigRow>> GetAllConfigAsync(string scope = "global", string? agentId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var sql = "SELECT key, value, scope, agent_id, updated_at FROM config WHERE scope = @scope";
            if (agentId is not null)
                sql += " AND agent_id = @sid";
            return (await _conn.QueryAsync<ConfigRow>(sql, new { scope, sid = agentId ?? "" })).AsList();
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> GetConfigAsync(string key, string scope = "global", string? agentId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var sid = agentId ?? "";
            return await _conn.QueryFirstOrDefaultAsync<string>(
                "SELECT value FROM config WHERE key = @key AND scope = @scope AND agent_id = @sid",
                new { key, scope, sid });
        }
        finally { _lock.Release(); }
    }

    public async Task UpsertConfigAsync(string key, string value, string scope = "global", string? agentId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            var sid = agentId ?? "";
            await _conn.ExecuteAsync("""
                INSERT INTO config (key, value, scope, agent_id, updated_at)
                VALUES (@key, @value, @scope, @sid, @now)
                ON CONFLICT(key, scope, agent_id) DO UPDATE SET value = @value, updated_at = @now
                """, new { key, value, scope, sid, now });
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteConfigAsync(string key, string scope = "global", string? agentId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var sid = agentId ?? "";
            await _conn.ExecuteAsync(
                "DELETE FROM config WHERE key = @key AND scope = @scope AND agent_id = @sid",
                new { key, scope, sid });
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> ConfigExistsAsync(string key, string scope = "global", string? agentId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var sid = agentId ?? "";
            return await _conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM config WHERE key = @key AND scope = @scope AND agent_id = @sid",
                new { key, scope, sid }) > 0;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> GetConfigCountAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await _conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM config");
        }
        finally { _lock.Release(); }
    }

    public async Task<Dictionary<string, string>> GetMergedConfigAsync(string? agentId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var sql = agentId is not null ? """
                SELECT key, value FROM config WHERE scope = 'global'
                UNION ALL
                SELECT key, value FROM config WHERE scope = 'session' AND agent_id = @sid
                """ : "SELECT key, value FROM config WHERE scope = 'global'";
            var rows = await _conn.QueryAsync<(string Key, string Value)>(sql, new { sid = agentId ?? "" });
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in rows)
                merged[key] = value;
            return merged;
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> GetLastSessionIdAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await _conn.QueryFirstOrDefaultAsync<string>(
                "SELECT agent_id FROM blocks ORDER BY created_at DESC LIMIT 1");
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> GetSessionIdByWorkingDirectoryAsync(string cwd)
    {
        await _lock.WaitAsync();
        try
        {
            return await _conn.QueryFirstOrDefaultAsync<string>(
                "SELECT agent_id FROM config WHERE key = 'Session:WorkingDirectory' AND value = @cwd AND scope = 'session' ORDER BY updated_at DESC LIMIT 1",
                new { cwd });
        }
        finally { _lock.Release(); }
    }
}
