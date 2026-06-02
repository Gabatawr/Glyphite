using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Glyphite.Host.Services;

public class ConfigService : IConfigService
{
    private readonly IMemoryStore _store;
    private readonly IConfiguration _appConfig;

    public Action<string>? LogAction { get; set; }

    public ConfigService(IMemoryStore store, IConfiguration appConfig)
    {
        _store = store;
        _appConfig = appConfig;
    }

    /// <summary>Hydrate a typed options object from DB-stored flat config.</summary>
    public async Task<T> GetOptionsAsync<T>(string sectionName, string? sessionId = null) where T : new()
    {
        var all = await GetConfigAsync(sessionId);
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
    /// Seed DB from appsettings.json on startup. For each flat key in IConfiguration:
    ///   - if not in DB в†’ insert
    ///   - if value differs в†’ update (log mismatch)
    /// </summary>
    public async Task InitializeAsync()
    {
        var appKeys = FlattenConfig(_appConfig);

        foreach (var (key, value) in appKeys)
        {
            var masked = MaskValue(key, value);
            var existing = await _store.GetConfigAsync(key, "global");
            if (existing is null)
            {
                await _store.UpsertConfigAsync(key, value, "global");
                Log($"[config] seeded {key} = {masked}");
            }
            else if (existing != value)
            {
                var maskedExisting = MaskValue(key, existing);
                Log($"[config] mismatch {key}: DB=\"{maskedExisting}\" appsettings=\"{masked}\"");
                await _store.UpsertConfigAsync(key, value, "global");
                Log($"[config] updated {key} = {masked}");
            }
        }
    }

    public async Task<Dictionary<string, string>> GetConfigAsync(string? sessionId = null)
    {
        return await _store.GetMergedConfigAsync(sessionId);
    }

    public async Task<ConfigDiffResult> UpdateConfigAsync(
        Dictionary<string, string> changes,
        string scope = "global",
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var updated = new Dictionary<string, string>();
        var skipped = new Dictionary<string, string>();

        foreach (var (key, newValue) in changes)
        {
            var existing = await _store.GetConfigAsync(key, scope, sessionId);
            if (existing == newValue)
            {
                skipped[key] = newValue;
                continue;
            }

            await _store.UpsertConfigAsync(key, newValue, scope, sessionId);
            updated[key] = newValue;
        }

        return new ConfigDiffResult(updated, skipped);
    }

    public async Task DeleteConfigAsync(string[] keys, string scope = "global", string? sessionId = null)
    {
        foreach (var key in keys)
            await _store.DeleteConfigAsync(key, scope, sessionId);
    }

    private void Log(string message)
    {
        Console.WriteLine(message);
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
                // nested section вЂ” recurse
                foreach (var (k, v) in FlattenConfig(section, key))
                    result[k] = v;
            }
        }

        return result;
    }
}
