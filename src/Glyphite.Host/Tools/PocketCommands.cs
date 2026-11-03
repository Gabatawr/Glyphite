using System.ComponentModel;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Services;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

/// <summary>
/// The five <c>pocket_*</c> management tools the model calls: list, add, set, remove, run.
/// Reading/listing/executing lives here; the editing half (add/set + the safety pre-flight) lives in
/// <c>PocketCommands.Edit.cs</c>.
/// </summary>
public static partial class PocketCommands
{
    private static readonly HashSet<string> KnownBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "bash_back", "read_file", "write_file", "patch_file", "todo", "fetch_web",
        "search_glob", "search_grep", "kvstore", "memory", "subagent_run", "subagent_use",
        "subagent_list", "pocket_list", "pocket_add", "pocket_set", "pocket_remove", "pocket_run",
    };

    /// <summary>Fields that <c>pocket_set</c> can change — one field per call.</summary>
    private static readonly HashSet<string> SettableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "materialize", "name", "scope", "category", "args", "cwd", "desc", "cmd",
    };

    /// <summary>All five pocket_* management tools (list/add/set/remove/run).</summary>
    public static IReadOnlyList<AIFunction> AsManagementFunctions(
        IKVStore kvStore, IConfigService cfg, IBashSessionManager bashManager,
        SubAgentManager subAgentManager, ISafetyChecker? safetyChecker,
        string agentId, bool isSubAgent)
    {
        var runner = PocketExecution.CreateRunner(bashManager, cfg, kvStore, isSubAgent, safetyChecker, agentId);
        var isEphemeral = subAgentManager.IsEphemeral(agentId);

        return
        [
            AIFunctionFactory.Create(async (
                    [Description("Optional glob filter on tool name (* and ? supported).")] string? pattern = null,
                    [Description("Optional category filter (exact match, case-insensitive).")] string? category = null,
                    [Description("If true, shows full details (command, args, cwd, created) for every entry. Default false: compact list without descriptions.")] bool details = false,
                    CancellationToken ct = default) =>
                    await HandleList(kvStore, agentId, pattern, category, details),
                name: "pocket_list",
                description: "List pocket tools — user-defined aliases stored in the agent's vault. Compact by default (names grouped by category, no descriptions); pass details=true for full entry details. Optionally filter by glob pattern on name and/or by exact category."),

            AIFunctionFactory.Create(async (
                    [Description("Tool alias/name — lowercase [a-z0-9_], max 64 chars. Will appear as the native tool name.")] string name,
                    [Description("What this pocket tool does.")] string desc,
                    [Description("Bash command template with {arg} placeholders, e.g. \"manimgl {file} {scene} -w\".")] string cmd,
                    [Description("Optional JSON array of arg definitions, e.g. [{\"name\":\"file\",\"desc\":\"scene file\",\"req\":true,\"def\":\"default\",\"raw\":false}]. \"raw\":true inserts the value without shell quoting.")] string? args = null,
                    [Description("Optional working directory for the command.")] string? cwd = null,
                    [Description("Optional category for grouping, e.g. \"manim\", \"research\". Default: \"common\".")] string? category = null,
                    [Description("If true, the entry becomes a native typed tool in the agent's toolset (favorite). Default false: stays in the pocket, run via pocket_run.")] bool materialize = false,
                    [Description("Scope: 'local' (default — this agent's vault only) or 'global' (shared — visible, editable and executable by all agents; materialize=true then exposes the native tool for ALL agents).")] string? scope = null,
                    CancellationToken ct = default) =>
                    await HandleAdd(cfg, kvStore, agentId, isEphemeral, isSubAgent, safetyChecker,
                        name, desc, cmd, args, cwd, category, materialize, scope, ct),
                name: "pocket_add",
                description: "Add a pocket tool immediately (category defaults to \"common\", scope defaults to \"local\" — this agent's vault; scope=\"global\" creates a shared tool visible to all agents. materialize defaults to false — pocket-only, run via pocket_run; set materialize=true to also expose it as a native typed tool named '<name>_pocket' on the next turn (for a global entry — for all agents)). If the name already exists in the target scope — error: remove it first (pocket_remove), then add again (no overwrite). Adding a local entry that shadows an existing global one is allowed — the local takes precedence for this agent. The command template is safety-checked once at add time: forbidden-command/directory hard blocks, plus an interactive confirmation prompt if it matches the configured CheckRequireCommands list. If approved, executions skip the interactive prompt (hard blocks still apply at run time). Values are shell-quoted on substitution unless the arg has raw=true. Takes effect at once."),

            AIFunctionFactory.Create(async (
                    [Description("Name of the pocket tool to modify.")] string name,
                    [Description("Field to change: materialize, name, scope, category, args, cwd, desc, cmd.")] string arg,
                    [Description("New value for the field. materialize: 'true' or 'false'. scope: 'local' or 'global' (moves the entry between scopes). args: JSON array string, e.g. [{\"name\":\"file\",\"desc\":\"scene file\",\"req\":true}]. cwd: empty string clears the field. Others: plain text.")] string value,
                    [Description("Which entry to modify: omitted = effective (local first, else global); 'local' = this agent's entry only; 'global' = the shared entry explicitly, even if shadowed. For arg='scope' this selects the SOURCE entry to move.")] string? scope = null,
                    CancellationToken ct = default) =>
                    await HandleSet(cfg, kvStore, agentId, isEphemeral, isSubAgent, safetyChecker, name, arg, value, scope, ct),
                name: "pocket_set",
                description: "Update one field of an existing pocket tool (partial update — one field per call). Fields: materialize ('true'/'false' toggles the native '_pocket' tool), name (rename; target must be free — no overwrite), scope ('global' moves a local entry to the shared space and vice versa; collision in the target scope blocks the move), category, args (JSON array of arg definitions, same format as pocket_add), cwd (empty string clears), desc, cmd. By default operates on the effective entry (local first, else global); pass scope='global' to edit the shared entry explicitly even when a local shadow exists. Changing cmd re-runs the add-time safety preflight (forbidden checks + interactive confirmation); if the new template is rejected, nothing changes. Other fields keep Approved and Created. Takes effect at once; native (materialized) tools change on the next turn."),

            AIFunctionFactory.Create(async (
                    [Description("Name of the pocket tool to remove.")] string name,
                    [Description("Which entry to remove: omitted = effective (local first — removes the local shadow only, the global stays; else the global); 'local' = this agent's entry only; 'global' = the shared entry explicitly.")] string? scope = null,
                    CancellationToken ct = default) =>
                    await HandleRemove(kvStore, agentId, isEphemeral, name, scope),
                name: "pocket_remove",
                description: "Remove a pocket tool immediately. By default removes the effective entry (local first — a local shadow is removed and the global tool stays; if no local entry exists, the global one is removed). Pass scope='global' to remove the shared entry explicitly, even when a local shadow exists. Errors if it doesn't exist. Takes effect at once; the native tool disappears on the next turn."),

            AIFunctionFactory.Create(async (
                    [Description("Name of the pocket tool to execute.")] string tool,
                    [Description("Optional JSON object of arg values, e.g. {\"file\":\"scene.py\",\"scene\":\"Scene\"}.")] string? args = null,
                    CancellationToken ct = default) =>
                    await HandleRun(kvStore, runner, agentId, tool, args, ct),
                name: "pocket_run",
                description: "Execute a pocket tool immediately by name (same-turn, no reload needed). Runs the entry's command template via bash with the standard safety checks. Use for pocket entries that are not materialized as native tools."),
        ];
    }

    private static async Task<string> HandleList(IKVStore kvStore, string agentId, string? pattern, string? category, bool details)
    {
        var local = await PocketStore.LoadEntriesAsync(kvStore, agentId);
        var global = await PocketStore.LoadGlobalEntriesAsync(kvStore);
        var localNames = new HashSet<string>(local.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var items = new List<(PocketEntry Entry, bool Global, bool Shadowed)>();
        items.AddRange(local.Select(e => (e, false, false)));
        items.AddRange(global.Select(g => (g, true, localNames.Contains(g.Name))));

        if (!string.IsNullOrWhiteSpace(pattern) && pattern != "*")
            items = items.Where(t => GlobHelper.ToRegex(pattern).IsMatch(t.Entry.Name)).ToList();

        if (!string.IsNullOrWhiteSpace(category) && category != "*")
        {
            var cat = PocketEntry.NormalizeCategory(category);
            items = items.Where(t => string.Equals(t.Entry.Category, cat, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var sb = new StringBuilder();
        if (items.Count == 0)
        {
            sb.AppendLine("(empty — no pocket tools. Use pocket_add to create one.)");
        }
        else if (details)
        {
            var localCount = items.Count(t => !t.Global);
            var globalCount = items.Count - localCount;
            var scopeInfo = globalCount > 0 ? $" ({localCount} local, {globalCount} global)" : "";
            sb.AppendLine($"Pocket: {items.Count} tool(s){scopeInfo}");
            for (var i = 0; i < items.Count; i++)
            {
                sb.AppendLine($"\n[{i + 1}]");
                sb.AppendLine(PocketSchema.FormatEntry(items[i].Entry, items[i].Global, items[i].Shadowed));
            }
        }
        else
        {
            var groups = items
                .GroupBy(t => t.Entry.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var catWord = groups.Count == 1 ? "category" : "categories";
            sb.AppendLine($"Pocket: {items.Count} tool(s) in {groups.Count} {catWord}");
            var i = 0;
            foreach (var g in groups)
            {
                sb.AppendLine($"\n  [{g.Key}]");
                foreach (var t in g)
                {
                    i++;
                    var e = t.Entry;
                    var argsInfo = e.Args.Count == 0 ? "no args" : $"{e.Args.Count} arg(s)";
                    var markers = new List<string>();
                    if (e.Materialize) markers.Add("native");
                    if (t.Global) markers.Add("global");
                    if (t.Shadowed) markers.Add("shadowed");
                    var m = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";
                    sb.AppendLine($"    [{i}] {e.Name} ({argsInfo}){m}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<string> HandleRemove(IKVStore kvStore, string agentId, bool isEphemeral, string name, string? scope)
    {
        if (isEphemeral)
            return "Error: pocket tools are not persisted for ephemeral agents.";

        var n = PocketEntry.NormalizeName(name);
        var explicitScope = !string.IsNullOrWhiteSpace(scope);
        var sc = PocketStore.NormalizeScope(scope);
        if (!PocketStore.IsValidScope(sc))
            return $"Error: invalid scope '{scope}'. Use 'local' or 'global'.";

        if (explicitScope)
        {
            var targetAgentId = PocketStore.NamespaceFor(sc, agentId);
            var existing = await PocketStore.GetEntryAsync(kvStore, targetAgentId, n);
            if (existing is null)
                return $"Error: no {sc} pocket tool '{n}' — nothing to remove. Use pocket_list to see available tools.";
            await PocketStore.DeleteAsync(kvStore, targetAgentId, n);
            return sc == PocketStore.ScopeGlobal
                ? $"✓ Global pocket tool '{n}' removed (shared — gone for all agents)."
                : $"✓ Pocket tool '{n}' removed.";
        }

        var local = await PocketStore.GetEntryAsync(kvStore, agentId, n);
        if (local is not null)
        {
            await PocketStore.DeleteAsync(kvStore, agentId, n);
            return $"✓ Pocket tool '{n}' removed (local shadow — the global tool, if any, stays for others).";
        }

        var global = await PocketStore.GetGlobalEntryAsync(kvStore, n);
        if (global is not null)
        {
            await PocketStore.DeleteAsync(kvStore, PocketStore.GlobalAgentId, n);
            return $"✓ Global pocket tool '{n}' removed (shared — gone for all agents).";
        }

        return $"Error: no pocket tool '{n}' — nothing to remove. Use pocket_list to see available tools.";
    }

    private static async Task<string> HandleRun(
        IKVStore kvStore, PocketExecution.PocketRunner runner, string agentId, string tool, string? argsJson, CancellationToken ct)
    {
        var n = PocketEntry.NormalizeName(tool);
        var entry = await PocketStore.GetEffectiveEntryAsync(kvStore, agentId, n);
        if (entry is null)
            return $"No pocket tool '{tool}'. Use pocket_list to see available tools.";

        var args = PocketSchema.ParseArgsObject(argsJson);
        if (args is null)
            return "Error: 'args' must be a JSON object, e.g. {\"file\":\"scene.py\"}.";

        return await runner(entry, args, ct);
    }
}
