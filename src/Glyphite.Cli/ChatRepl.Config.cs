namespace Glyphite.Cli;

public partial class ChatRepl
{
    private async Task LoadAgentConfigAsync(string agentId, string cwd)
    {
        var merge = await ReadAgentConfigFilesAsync(agentId, cwd);
        if (merge.Count == 0) return;

        var homePath = await _store.GetAgentHomePathAsync(agentId);
        if (string.Equals(homePath, cwd, StringComparison.OrdinalIgnoreCase))
        {
            // Home → persist to DB
            await _cfgService.UpdateConfigAsync(merge, scope: "session", sessionId: agentId);
        }
        else
        {
            // Non-home → in-memory overlay (ConfigService applies it in GetConfigAsync)
            _cfgService.SetSessionOverlay(agentId, merge);
        }
    }

    /// <summary>Scan cwd for Glyphite.{agentName}.json files, validate against known agents, load into in-memory overlay.</summary>
    private async Task LoadAllAgentConfigsAsync(string cwd)
    {
        var agents = await _store.ListAgentsAsync();
        var agentSet = new HashSet<string>(agents, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(cwd, "Glyphite.*.json"))
        {
            var fileName = Path.GetFileName(file);
            if (!fileName.StartsWith("Glyphite.", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var agentName = fileName["Glyphite.".Length..^".json".Length];
            if (string.IsNullOrEmpty(agentName) || !agentSet.Contains(agentName))
                continue;

            var merge = await ReadAgentConfigFilesAsync(agentName, cwd);
            if (merge.Count > 0)
            {
                var homePath = await _store.GetAgentHomePathAsync(agentName);
                if (string.Equals(homePath, cwd, StringComparison.OrdinalIgnoreCase))
                    await _cfgService.UpdateConfigAsync(merge, scope: "session", sessionId: agentName);
                else
                    _cfgService.SetSessionOverlay(agentName, merge);
            }
        }
    }

    private async Task<Dictionary<string, string>> ReadAgentConfigFilesAsync(string agentId, string cwd)
    {
        var merge = new Dictionary<string, string>();
        var glPath = Path.Combine(cwd, "Glyphite.json");
        if (File.Exists(glPath))
        {
            var glJson = await File.ReadAllTextAsync(glPath);
            var glConfig = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(glJson);
            if (glConfig is not null && glConfig.TryGetValue("Glyphite", out var gl) && gl is System.Text.Json.JsonElement el)
                FlattenJsonElement("Glyphite", el, merge);
        }

        var agentPath = Path.Combine(cwd, $"Glyphite.{agentId}.json");
        if (File.Exists(agentPath))
        {
            var agentJson = await File.ReadAllTextAsync(agentPath);
            var agentConfig = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(agentJson);
            if (agentConfig is not null && agentConfig.TryGetValue("Glyphite", out var ac) && ac is System.Text.Json.JsonElement ael)
                FlattenJsonElement("Glyphite", ael, merge);
        }

        return merge;
    }

    private static void FlattenJsonElement(string prefix, System.Text.Json.JsonElement el, Dictionary<string, string> result)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    FlattenJsonElement($"{prefix}:{prop.Name}", prop.Value, result);
                break;
            case System.Text.Json.JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    FlattenJsonElement($"{prefix}:{i++}", item, result);
                break;
            case System.Text.Json.JsonValueKind.String:
                result[prefix] = el.GetString() ?? "";
                break;
            case System.Text.Json.JsonValueKind.Number:
                result[prefix] = el.GetRawText();
                break;
            case System.Text.Json.JsonValueKind.True:
                result[prefix] = "True";
                break;
            case System.Text.Json.JsonValueKind.False:
                result[prefix] = "False";
                break;
            case System.Text.Json.JsonValueKind.Null:
                break;
        }
    }
}
