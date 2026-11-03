using System.Collections.Concurrent;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Glyphite.Host.Services;

public class ConfigService : IConfigService, IDisposable
{
    private readonly IConfigStore _store;
    private readonly IConfiguration _appConfig;
    private readonly ILogger<ConfigService> _logger;
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _overlays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-agent overrides only — <b>not</b> the merged view. Global keys are deliberately kept
    /// out of this cache: they come from <see cref="_appConfig"/>, which hot-reloads, and caching them
    /// together with the session part pinned every per-agent read to whatever the file said when the
    /// agent first asked. The cache now holds only what is expensive (a DB round-trip).</summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _sessionCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IDisposable? _reloadSubscription;
    private readonly object _globalGate = new();
    private volatile bool _globalStale = true;
    private Dictionary<string, string>? _globalConfig;

    public ConfigService(IConfigStore store, IConfiguration appConfig, ILogger<ConfigService>? logger = null)
    {
        _store = store;
        _appConfig = appConfig;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigService>.Instance;

        // `AddJsonFile(reloadOnChange: true)` updates `_appConfig` in place and announces it only through
        // the reload token — nothing else says "the file changed". Without this subscription the flattened
        // snapshot below would keep startup values forever, which is exactly how an edited Glyphite.json
        // looked ignored.
        _reloadSubscription = ChangeToken.OnChange(
            () => _appConfig.GetReloadToken(),
            () => _globalStale = true);
    }

    public void Dispose() => _reloadSubscription?.Dispose();

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
        // Global values need no hook here: they are re-read when the reload token fires.
        if (agentId is null)
            _sessionCache.Clear();
        else
            _sessionCache.TryRemove(agentId, out _);
    }

    public async Task<Dictionary<string, string>> GetConfigAsync(string? agentId = null)
    {
        // Always a copy — callers own the result and merge into it.
        var merged = new Dictionary<string, string>(GetGlobalConfig(), StringComparer.OrdinalIgnoreCase);
        if (agentId is null)
            return merged;

        var overrides = _sessionCache.TryGetValue(agentId, out var cached)
            ? cached
            : await LoadSessionOverridesAsync(agentId);

        foreach (var (key, value) in overrides)
            merged[key] = value;
        return merged;
    }

    /// <summary>Global keys as the files have them right now. <c>_appConfig</c> is a live view, so the only
    /// thing worth remembering is the flattened form plus a flag that it went stale.</summary>
    private Dictionary<string, string> GetGlobalConfig()
    {
        if (!_globalStale && _globalConfig is { } cached)
            return cached;

        lock (_globalGate)
        {
            if (!_globalStale && _globalConfig is { } snapshot)
                return snapshot;

            // Take the token *before* flattening: a reload landing mid-walk would otherwise be baked into
            // a half-old snapshot with no second event left to correct it.
            var token = _appConfig.GetReloadToken();
            var flattened = FlattenConfig(_appConfig);
            _globalConfig = flattened;
            _globalStale = token.HasChanged;
            return flattened;
        }
    }

    /// <summary>What one agent overrides, cached: its DB session rows, or the loader's in-memory overlay
    /// when the agent is not at home (that overlay already carries those rows <i>and</i> its own directory
    /// files). Global keys are never part of this — see <see cref="_sessionCache"/>.</summary>
    private async Task<Dictionary<string, string>> LoadSessionOverridesAsync(string agentId)
    {
        Dictionary<string, string> overrides;

        if (_overlays.TryGetValue(agentId, out var overlay))
            overrides = new Dictionary<string, string>(overlay, StringComparer.OrdinalIgnoreCase);
        else
            overrides = await _store.GetSessionConfigAsync(agentId);

        _sessionCache[agentId] = overrides;
        return overrides;
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

        // If overlay is present (agent not at home), apply changes immediately
        // otherwise they won't be visible this turn until session reloads.
        if (agentId is not null && _overlays.TryGetValue(agentId, out var overlay))
        {
            foreach (var (key, value) in updated)
                overlay[key] = value;
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
