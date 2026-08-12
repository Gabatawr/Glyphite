using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glyphite.Cli;

/// <summary>
/// First-run configuration flow, executed before the host is built:
/// 1. Ensures a global <c>Glyphite.json</c> exists in the install location
///    (<c>AppContext.BaseDirectory</c> — <c>~/.glyphite/</c> for the published app).
/// 2. If no API key is configured anywhere, prompts the user to either enter a key
///    (saved to the global config) or create a local <c>Glyphite.json</c> in the
///    working directory. This is the ONLY place a config file is auto-created in a
///    working directory.
/// 3. If the global config has a key but the current folder has no local config,
///    asks once per folder whether to use the global key or create a local file.
///    The choice is remembered in <c>state.json</c> next to the global config, so
///    the prompt appears only once per folder.
/// Non-interactive contexts (redirected stdin/stdout) skip all prompts and silently
/// adopt the global config without creating any files.
/// </summary>
public static class FirstRunConfig
{
    private const string ConfigFileName = "Glyphite.json";
    private const string StateFileName = "state.json";
    private const string EmbeddedConfigResource = "Glyphite.Cli.appsettings.json";

    public static void Ensure()
    {
        try
        {
            var installDir = AppContext.BaseDirectory;
            var globalPath = Path.Combine(installDir, ConfigFileName);

            // 1. Global config must exist — create a template from the embedded defaults.
            //    publish.sh also creates it at install time; this covers manual installs.
            if (!File.Exists(globalPath))
                WriteEmbeddedTemplate(globalPath);

            var cwd = Directory.GetCurrentDirectory();
            var cwdConfig = Path.Combine(cwd, ConfigFileName);
            if (File.Exists(cwdConfig))
                return; // local config present — it overrides global; nothing to ask

            var globalKey = ReadApiKey(globalPath);

            if (string.IsNullOrWhiteSpace(globalKey))
            {
                // No key anywhere — guide the user on first launch.
                if (CanPrompt())
                    PromptNoKey(globalPath, cwdConfig);
                return; // non-interactive: leave as-is; turns will report the missing key
            }

            // Global key exists — decide once per folder: use global or create local.
            if (IsUseGlobalFolder(installDir, cwd))
                return;

            if (CanPrompt())
                PromptUseGlobalOrLocal(globalPath, cwdConfig, installDir, cwd);
            else
                AddUseGlobalFolder(installDir, cwd); // non-interactive: silently adopt global
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] First-run config check failed: {ex.Message}");
        }
    }

    // ── Prompts ─────────────────────────────────────────────────────

    private static bool CanPrompt()
    {
        try { return !Console.IsInputRedirected && !Console.IsOutputRedirected; }
        catch { return false; }
    }

    private static void PromptNoKey(string globalPath, string cwdConfig)
    {
        Console.WriteLine();
        Console.WriteLine("No API key configured.");
        Console.WriteLine("  [1] Enter API key — saved to the global config (works in every folder):");
        Console.WriteLine($"      {globalPath}");
        Console.WriteLine("  [2] Create Glyphite.json in this folder — enter API key:");
        Console.WriteLine($"      {cwdConfig}");
        Console.WriteLine("  [3] Skip — configure later (turns will fail without a key)");
        Console.Write("Choose [1/2/3]: ");
        var choice = Console.ReadLine()?.Trim();

        if (choice == "1")
        {
            Console.Write("API key: ");
            var key = Console.ReadLine()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(key))
                WriteApiKey(globalPath, key);
        }
        else if (choice == "2")
        {
            CreateLocalConfigWithKey(cwdConfig);
        }
        Console.WriteLine();
    }

    private static void PromptUseGlobalOrLocal(string globalPath, string cwdConfig, string installDir, string cwd)
    {
        Console.WriteLine();
        Console.WriteLine($"Global config found ({globalPath}) — API key is set.");
        Console.WriteLine("  [1] Use global config for this folder (won't ask here again)");
        Console.WriteLine("  [2] Create Glyphite.json in this folder — enter API key (per-project overrides)");
        Console.Write("Choose [1/2] (default 1): ");
        var choice = Console.ReadLine()?.Trim();

        if (choice == "2")
            CreateLocalConfigWithKey(cwdConfig);
        else
            AddUseGlobalFolder(installDir, cwd);
        Console.WriteLine();
    }

    // ── Global config IO ────────────────────────────────────────────

    private static string GetEmbeddedTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedConfigResource)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedConfigResource}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteEmbeddedTemplate(string path)
    {
        File.WriteAllText(path, GetEmbeddedTemplate());
    }

    /// <summary>Create a local config from the template and immediately prompt for the
    /// API key (same UX as the global flow) — no need to edit the file afterwards.</summary>
    private static void CreateLocalConfigWithKey(string path)
    {
        WriteEmbeddedTemplate(path);
        Console.Write("API key: ");
        var key = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(key))
            WriteApiKey(path, key);
    }

    private static string? ReadApiKey(string globalPath)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(globalPath));
            return root?["Glyphite"]?["LLM"]?["ApiKey"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteApiKey(string globalPath, string key)
    {
        // Targeted in-place replacement of the ApiKey value — preserves the original
        // template formatting byte-for-byte. Re-serializing the whole document (JsonNode
        // + WriteIndented) would reformat it differently from appsettings.json.
        var json = File.ReadAllText(globalPath);
        var escaped = JsonSerializer.Serialize(key); // properly quoted + escaped JSON string
        var match = Regex.Match(
            json,
            "\"ApiKey\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (match.Success)
        {
            var updated = string.Concat(
                json.AsSpan(0, match.Index),
                "\"ApiKey\": ", escaped,
                json.AsSpan(match.Index + match.Length));
            File.WriteAllText(globalPath, updated);
            return;
        }

        // No ApiKey property found (template changed?) — fall back to re-serialization.
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var glyphite = root["Glyphite"] as JsonObject ?? new JsonObject();
        root["Glyphite"] = glyphite;
        var llm = glyphite["LLM"] as JsonObject ?? new JsonObject();
        glyphite["LLM"] = llm;
        llm["ApiKey"] = key;
        File.WriteAllText(globalPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Per-folder state (state.json next to the global config) ─────

    private static string StatePath(string installDir) => Path.Combine(installDir, StateFileName);

    private static bool IsUseGlobalFolder(string installDir, string cwd)
    {
        try
        {
            var path = StatePath(installDir);
            if (!File.Exists(path)) return false;
            var root = JsonNode.Parse(File.ReadAllText(path));
            var list = root?["FirstRun"]?["UseGlobalFolders"]?.AsArray();
            if (list is null) return false;
            var full = Path.GetFullPath(cwd);
            foreach (var item in list)
            {
                var p = item?.GetValue<string>();
                if (p is not null && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false; // unreadable state — treat as not answered; the prompt may reappear
        }
    }

    private static void AddUseGlobalFolder(string installDir, string cwd)
    {
        try
        {
            var path = StatePath(installDir);
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();

            var firstRun = root["FirstRun"] as JsonObject ?? new JsonObject();
            root["FirstRun"] = firstRun;
            var list = firstRun["UseGlobalFolders"] as JsonArray ?? new JsonArray();
            firstRun["UseGlobalFolders"] = list;

            var full = Path.GetFullPath(cwd);
            foreach (var item in list)
            {
                var p = item?.GetValue<string>();
                if (p is not null && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase))
                    return; // already present
            }
            list.Add(full);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort — a failure just means the prompt may appear again in this folder.
        }
    }
}
