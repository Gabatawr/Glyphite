using Glyphite.Abstractions.Interfaces;
using Dapper;

namespace Glyphite.Host.Data;

public class KVStoreRepository : RepositoryBase, IKVStore
{
    public KVStoreRepository(string connectionString) : base(connectionString)
    {
        Initialize();
    }

    private void Initialize()
    {
        _conn.Execute("""
            CREATE TABLE IF NOT EXISTS kv_store (
                agent_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                ttl INTEGER,
                expires_at TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (agent_id, key)
            );
            """);
    }

    public async Task<string?> GetAsync(string agentId, string key)
    {
        using var conn = CreateReadConnection();
        var row = await conn.QueryFirstOrDefaultAsync<(string Value, string? ExpiresAt)>(
            "SELECT value, expires_at FROM kv_store WHERE agent_id = @agentId AND key = @key",
            new { agentId, key });

        if (row == default) return null;

        // Check expiration
        if (!string.IsNullOrEmpty(row.ExpiresAt))
        {
            if (DateTime.TryParse(row.ExpiresAt, out var expires) && expires <= DateTime.UtcNow)
                return null; // expired — treat as not found; purge on next write
        }

        return row.Value;
    }

    public async Task<Dictionary<string, string>> ListAsync(string agentId, string? keyPattern = null)
    {
        // Use the write connection since we may purge expired keys
        await PurgeExpiredAsync(agentId);

        using var conn = CreateReadConnection();

        if (string.IsNullOrEmpty(keyPattern) || keyPattern == "*")
        {
            var rows = await conn.QueryAsync<(string Key, string Value)>(
                "SELECT key, value FROM kv_store WHERE agent_id = @agentId",
                new { agentId });
            return ToDict(rows);
        }

        var sqlPattern = GlobToLike(keyPattern);
        var matched = await conn.QueryAsync<(string Key, string Value)>(
            "SELECT key, value FROM kv_store WHERE agent_id = @agentId AND key LIKE @pattern ESCAPE '\\'",
            new { agentId, pattern = sqlPattern });
        return ToDict(matched);
    }

    public async Task SetAsync(string agentId, string key, string value, int? ttlSeconds = null)
    {
        // Empty value = delete key (dead key, no useful info)
        if (value.Length == 0 && !ttlSeconds.HasValue)
        {
            await DeleteAsync(agentId, key);
            return;
        }

        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            string? expiresAt = null;

            if (ttlSeconds.HasValue && ttlSeconds.Value > 0)
                expiresAt = DateTime.UtcNow.AddSeconds(ttlSeconds.Value).ToString("O");

            await _conn.ExecuteAsync("""
                INSERT INTO kv_store (agent_id, key, value, ttl, expires_at, updated_at)
                VALUES (@agentId, @key, @value, @ttl, @expiresAt, @now)
                ON CONFLICT(agent_id, key) DO UPDATE SET
                    value = @value,
                    ttl = @ttl,
                    expires_at = @expiresAt,
                    updated_at = @now
                """, new { agentId, key, value, ttl = ttlSeconds, expiresAt, now });
        });
    }

    public async Task DeleteAsync(string agentId, string key)
    {
        await WithLockAsync(async () =>
        {
            await _conn.ExecuteAsync(
                "DELETE FROM kv_store WHERE agent_id = @agentId AND key = @key",
                new { agentId, key });
        });
    }

    public async Task<int> DeleteByPatternAsync(string agentId, string keyPattern)
    {
        return await WithLockAsync(async () =>
        {
            var sqlPattern = GlobToLike(keyPattern);
            return await _conn.ExecuteAsync(
                "DELETE FROM kv_store WHERE agent_id = @agentId AND key LIKE @pattern ESCAPE '\\'",
                new { agentId, pattern = sqlPattern });
        });
    }

    /// <summary>Remove expired keys for an agent.</summary>
    private async Task PurgeExpiredAsync(string agentId)
    {
        await WithLockAsync(async () =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await _conn.ExecuteAsync(
                "DELETE FROM kv_store WHERE agent_id = @agentId AND expires_at IS NOT NULL AND expires_at <= @now",
                new { agentId, now });
        });
    }

    /// <summary>Convert glob-style wildcards to SQL LIKE patterns.
    /// Escapes literal LIKE meta-characters (% _) then converts glob wildcards (* → %, ? → _).</summary>
    private static string GlobToLike(string pattern)
    {
        return pattern
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("*", "%")
            .Replace("?", "_");
    }

    private static Dictionary<string, string> ToDict(IEnumerable<(string Key, string Value)> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in rows)
            result[k] = v;
        return result;
    }
}
