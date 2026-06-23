using System.Collections.Concurrent;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

public class ConfigService : IConfigService
{
    private readonly IConfigStore _store;
    private readonly IConfiguration _appConfig;
    private readonly ILogger<ConfigService> _logger;
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _configCache = new(StringComparer.OrdinalIgnoreCase);

    public Action<string>? LogAction { get; set; }

    public ConfigService(IConfigStore store, IConfiguration appConfig, ILogger<ConfigService>? logger = null)
    {
        _store = store;
        _appConfig = appConfig;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigService>.Instance;
    }

    public void SetSessionOverlay(string sessionId, Dictionary<string, string> config)
    {
        _overlays[sessionId] = config;
        InvalidateCache(sessionId);
    }

    public void ClearSessionOverlay(string sessionId)
    {
        _overlays.TryRemove(sessionId, out _);
        InvalidateCache(sessionId);
    }

    /// <summary>Hydrate a typed options object: global keys from IConfiguration (hot-reload),
    /// session-scoped keys from DB + overlay.</summary>
    public async Task<T> GetOptionsAsync<T>(string sectionName, string? agentId = null) where T : new()
    {
        var all = await GetConfigAsync(agentId);
        var filtered = new Dictionary<string, string?>();
        var prefix = sectionName + ":";

        foreach (var (key, value) in all)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                filtered[key[prefix.Length..]] = value;

        if (filtered.Count == 0)
            return new T();

        return new ConfigurationBuilder()
            .AddInMemoryCollection(filtered)
            .Build()
            .Get<T>() ?? new T();
    }

    /// <summary>
    /// Clean stale session-scoped DB keys that no longer exist in IConfiguration.
    /// Global config is no longer seeded to DB — read directly from <see cref="_appConfig"/>.
    /// </summary>
    public async Task InitializeAsync(HashSet<string>? replaceSections = null)
    {
        var appKeys = FlattenConfig(_appConfig);

        var managedPrefixes = replaceSections?
            .Select(s => s.EndsWith(':') ? s : s + ':')
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (managedPrefixes is not null && managedPrefixes.Count > 0)
        {
            var allDb = await _store.GetMergedConfigAsync(null); // only global scope keys
            foreach (var (key, _) in allDb)
            {
                var prefix = managedPrefixes.FirstOrDefault(mp =>
                    key.StartsWith(mp, StringComparison.OrdinalIgnoreCase));
                if (prefix is null) continue;

                if (!appKeys.ContainsKey(key))
                {
                    await _store.DeleteConfigAsync(key, "global");
                    Log($"[config] removed stale {key}");
                }
            }
        }
    }

    public void InvalidateCache(string? agentId = null)
    {
        if (agentId is null)
            _configCache.Clear();
        else
            _configCache.TryRemove(agentId, out _);
    }

    public Task<Dictionary<string, string>> GetConfigAsync(string? agentId = null)
    {
        if (agentId is null)
        {
            // Global config: read hot-reloaded IConfiguration directly (no DB round-trip)
            return Task.FromResult(FlattenConfig(_appConfig));
        }

        // Session-scoped config: DB + overlay
        if (_configCache.TryGetValue(agentId, out var cached))
            return Task.FromResult(cached);

        return GetSessionConfigAsync(agentId);
    }

    private async Task<Dictionary<string, string>> GetSessionConfigAsync(string agentId)
    {
        // 1. Start with global config from IConfiguration (hot-reload)
        var merged = FlattenConfig(_appConfig);

        // 2. If overlay exists, it already includes DB session keys + file overrides
        //    from SessionConfigLoader → skip loading DB session keys separately.
        var hasOverlay = _overlays.ContainsKey(agentId);

        if (!hasOverlay)
        {
            // Get DB keys: GetMergedConfigAsync returns global + session.
            // We need only session-scoped keys (agent-specific overrides).
            // Stale global keys from DB must NOT override fresh IConfiguration.
            var dbAll = await _store.GetMergedConfigAsync(agentId);
            var dbGlobal = await _store.GetMergedConfigAsync(null);

            // Apply only session-scoped keys (those NOT in global scope)
            foreach (var (key, value) in dbAll)
            {
                if (!dbGlobal.ContainsKey(key))
                    merged[key] = value;
            }
        }

        // 3. Apply in-memory overlay (agent not at home, from SessionConfigLoader)
        if (_overlays.TryGetValue(agentId, out var overlay))
        {
            foreach (var (key, value) in overlay)
                merged[key] = value;
        }

        _configCache[agentId] = merged;
        return merged;
    }

    public async Task<ConfigDiffResult> UpdateConfigAsync(
        Dictionary<string, string> changes,
        string scope = "global",
        string? agentId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var updated = new Dictionary<string, string>();
        var skipped = new Dictionary<string, string>();

        foreach (var (key, newValue) in changes)
        {
            var existing = await _store.GetConfigAsync(key, scope, agentId);
            if (existing == newValue)
            {
                skipped[key] = newValue;
                continue;
            }

            await _store.UpsertConfigAsync(key, newValue, scope, agentId);
            updated[key] = newValue;
        }

        InvalidateCache(agentId);
        return new ConfigDiffResult(updated, skipped);
    }

    public async Task DeleteConfigAsync(string[] keys, string scope = "global", string? agentId = null)
    {
        foreach (var key in keys)
            await _store.DeleteConfigAsync(key, scope, agentId);
        InvalidateCache(agentId);
    }

    /// <summary>
    /// Atomically replace an entire config section: delete all old keys under
    /// <c>sectionName:</c> that are no longer present, then upsert the new ones.
    /// Use this instead of <see cref="UpdateConfigAsync"/> when the set of keys
    /// in a section has structurally changed (e.g. renamed MCP server names).
    /// </summary>
    public async Task ReplaceSectionAsync(string sectionName, Dictionary<string, string> newKeys, string scope = "global", string? agentId = null)
    {
        var prefix = sectionName + ":";

        // Gather existing keys under this section
        var all = await GetConfigAsync(agentId);
        var staleKeys = all.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Delete stale keys not in the new set
        foreach (var key in staleKeys)
        {
            if (!newKeys.ContainsKey(key))
                await _store.DeleteConfigAsync(key, scope, agentId);
        }

        // Upsert new keys
        foreach (var (key, value) in newKeys)
        {
            var existing = await _store.GetConfigAsync(key, scope, agentId);
            if (existing == value) continue;
            await _store.UpsertConfigAsync(key, value, scope, agentId);
        }

        InvalidateCache(agentId);
    }

    private void Log(string message)
    {
        _logger.LogInformation("{ConfigMessage}", message);
        LogAction?.Invoke(message);
    }

    private static string MaskValue(string key, string value)
    {
        if (key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Token", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length <= 8 ? "****" : $"{value[..4]}...{value[^4..]}";
        }
        return value;
    }

    private static Dictionary<string, string> FlattenConfig(IConfiguration config, string prefix = "")
    {
        var result = new Dictionary<string, string>();

        foreach (var section in config.GetChildren())
        {
            var key = string.IsNullOrEmpty(prefix) ? section.Key : $"{prefix}:{section.Key}";

            if (section.Value is not null)
            {
                // leaf value (simple config key)
                result[key] = section.Value;
            }
            else
            {
                // nested section — recurse
                foreach (var (k, v) in FlattenConfig(section, key))
                    result[k] = v;
            }
        }

        return result;
    }
}
