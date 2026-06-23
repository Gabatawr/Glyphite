using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Glyphite.Abstractions.Interfaces;

namespace Glyphite.Host.Services;

public class SessionConfigLoader : ISessionConfigLoader
{
    private readonly IConfigService _cfgService;
    private readonly IAgentStore _agentStore;
    private readonly IConfigStore _configStore;

    public SessionConfigLoader(IConfigService cfgService, IAgentStore agentStore, IConfigStore configStore)
    {
        _cfgService = cfgService;
        _agentStore = agentStore;
        _configStore = configStore;
    }

    public async Task LoadConfigAsync(string agentId, string agentCwd, string parentCwd)
    {
        var homePath = await _agentStore.GetAgentHomePathAsync(agentId);

        // ── STEP 0: If home directory no longer exists, adopt current cwd as new home ──
        // This handles the case where the original project/working directory was deleted.
        if (homePath is not null && !Directory.Exists(homePath))
        {
            homePath = agentCwd;
            await _agentStore.SetAgentHomePathAsync(agentId, homePath);
            // Clear stale session keys from the old (deleted) home
            await _configStore.DeleteConfigByScopeAsync("session", agentId);
        }

        // ── STEP 1: Always process home directory → update DB if changed ──
        // Home config is the agent's own settings, persisted to DB.
        // Parent/working dir keys are NEVER written to DB — they're inherited from files each time.
        if (homePath is not null)
        {
            var homeConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await ReadAndFlattenConfigFileAsync(Path.Combine(homePath, "Glyphite.json"), homeConfig);
            await ReadAndFlattenConfigFileAsync(Path.Combine(homePath, $"Glyphite.{agentId}.json"), homeConfig);

            // Get existing DB session keys for comparison
            var dbAll = await _configStore.GetMergedConfigAsync(agentId);
            var dbGlobal = await _configStore.GetMergedConfigAsync(null);
            var existingKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in dbAll)
                if (!dbGlobal.ContainsKey(key))
                    existingKeys[key] = value;

            // Change detection: only save if home config differs from DB
            var changed = existingKeys.Count != homeConfig.Count ||
                existingKeys.Any(kv => !homeConfig.TryGetValue(kv.Key, out var v) || v != kv.Value);

            if (changed)
            {
                await _configStore.DeleteConfigByScopeAsync("session", agentId);
                if (homeConfig.Count > 0)
                    await _cfgService.UpdateConfigAsync(homeConfig, scope: "session", agentId: agentId);
            }
        }

        // ── STEP 2: Build final merged config (DB → parent → cwd) ──
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 2a. Start with clean DB session keys (now only home-originated keys)
        var dbAll2 = await _configStore.GetMergedConfigAsync(agentId);
        var dbGlobal2 = await _configStore.GetMergedConfigAsync(null);
        foreach (var (key, value) in dbAll2)
            if (!dbGlobal2.ContainsKey(key))
                merged[key] = value;

        // 2b. Apply parent directory configs (override DB)
        await ReadAndFlattenConfigFileAsync(Path.Combine(parentCwd, "Glyphite.json"), merged);
        await ReadAndFlattenConfigFileAsync(Path.Combine(parentCwd, $"Glyphite.{agentId}.json"), merged);

        // 2c. Apply working directory configs if different from parent
        if (!string.Equals(agentCwd, parentCwd, StringComparison.OrdinalIgnoreCase))
        {
            await ReadAndFlattenConfigFileAsync(Path.Combine(agentCwd, "Glyphite.json"), merged);
            await ReadAndFlattenConfigFileAsync(Path.Combine(agentCwd, $"Glyphite.{agentId}.json"), merged);
        }

        // ── STEP 3: Set overlay if not at home ──
        // At home: no overlay needed — IConfiguration (hot-reload) has parent keys,
        // DB has home keys, GetSessionConfigAsync merges them correctly.
        if (!string.Equals(agentCwd, homePath, StringComparison.OrdinalIgnoreCase))
        {
            _cfgService.SetSessionOverlay(agentId, merged);
        }
    }

    private static async Task ReadAndFlattenConfigFileAsync(string filePath, Dictionary<string, string> target)
    {
        if (!File.Exists(filePath)) return;
        var json = await File.ReadAllTextAsync(filePath);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "Glyphite", StringComparison.OrdinalIgnoreCase))
                    FlattenJsonElement("", prop.Value, target);
            }
        }
    }

    private static string CombinePrefix(string prefix, string name)
    {
        return string.IsNullOrEmpty(prefix) ? name : $"{prefix}:{name}";
    }

    private static void FlattenJsonElement(string prefix, JsonElement el, Dictionary<string, string> result)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    FlattenJsonElement(CombinePrefix(prefix, prop.Name), prop.Value, result);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    FlattenJsonElement(CombinePrefix(prefix, $"{i++}"), item, result);
                break;
            case JsonValueKind.String:
                result[prefix] = el.GetString() ?? "";
                break;
            case JsonValueKind.Number:
                result[prefix] = el.GetRawText();
                break;
            case JsonValueKind.True:
                result[prefix] = "True";
                break;
            case JsonValueKind.False:
                result[prefix] = "False";
                break;
        }
    }
}
