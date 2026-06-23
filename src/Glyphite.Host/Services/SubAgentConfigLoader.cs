using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Glyphite.Abstractions.Interfaces;

namespace Glyphite.Host.Services;

public class SubAgentConfigLoader : ISubAgentConfigLoader
{
    private readonly IConfigService _cfgService;
    private readonly IAgentStore _agentStore;
    private readonly IConfigStore _configStore;

    public SubAgentConfigLoader(IConfigService cfgService, IAgentStore agentStore, IConfigStore configStore)
    {
        _cfgService = cfgService;
        _agentStore = agentStore;
        _configStore = configStore;
    }

    public async Task LoadConfigAsync(string agentId, string agentCwd, string parentCwd)
    {
        // Load all configs from parent directory
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await ReadAndFlattenConfigFileAsync(Path.Combine(parentCwd, "Glyphite.json"), merged);
        await ReadAndFlattenConfigFileAsync(Path.Combine(parentCwd, $"Glyphite.{agentId}.json"), merged);

        // Load configs from agent working directory (if different)
        if (!string.Equals(agentCwd, parentCwd, StringComparison.OrdinalIgnoreCase))
        {
            // Agent-specific configs override parent configs
            await ReadAndFlattenConfigFileAsync(Path.Combine(agentCwd, "Glyphite.json"), merged);
            await ReadAndFlattenConfigFileAsync(Path.Combine(agentCwd, $"Glyphite.{agentId}.json"), merged);
        }

        if (merged.Count == 0) return;

        var homePath = await _agentStore.GetAgentHomePathAsync(agentId);

        if (string.Equals(agentCwd, homePath, StringComparison.OrdinalIgnoreCase))
        {
            // Agent is at home → persist to DB (full replace session-scoped keys)
            // merged includes parent + agent configs with agent taking priority
            await _configStore.DeleteConfigByScopeAsync("session", agentId);
            await _cfgService.UpdateConfigAsync(merged, scope: "session", agentId: agentId);
        }
        else
        {
            // Agent is NOT at home → in-memory overlay only
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
