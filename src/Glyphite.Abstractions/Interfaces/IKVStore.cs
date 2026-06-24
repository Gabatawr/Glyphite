namespace Glyphite.Abstractions.Interfaces;

public interface IKVStore
{
    /// <summary>Get a single value by key. Returns null if key doesn't exist or is expired.</summary>
    Task<string?> GetAsync(string agentId, string key);

    /// <summary>List all keys matching a pattern (supports * and ? wildcards).</summary>
    Task<Dictionary<string, string>> ListAsync(string agentId, string? keyPattern = null);

    /// <summary>Set a key-value pair with optional TTL (in seconds).</summary>
    Task SetAsync(string agentId, string key, string value, int? ttlSeconds = null);

    /// <summary>Delete a key.</summary>
    Task DeleteAsync(string agentId, string key);

    /// <summary>Delete all keys matching a pattern.</summary>
    Task<int> DeleteByPatternAsync(string agentId, string keyPattern);
}
