using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

/// <summary>
/// "Pocket" — a per-agent toolbox of user-defined tool aliases stored in the agent's KV vault
/// (keys <c>pocket.&lt;name&gt;</c>, JSON values). Each entry wraps a bash command template with a
/// typed argument schema. Only entries flagged <c>Materialize</c> (favorites) are exposed as native
/// typed <see cref="AIFunction"/>s on the next tool load — named <c>&lt;name&gt;_pocket</c> so their
/// origin is explicit; all entries can be executed on demand via <c>pocket_run</c>.
/// Add/set/remove take effect immediately. Adding an existing name is an error — remove or rename (pocket_set name=) instead.
/// Command templates are safety-checked once at add time (forbidden checks + interactive
/// confirmation); approved entries skip the interactive prompt on subsequent executions.
/// Entries are <c>local</c> (this agent's vault) or <c>global</c> (shared — stored under the
/// <see cref="GlobalAgentId"/> sentinel, visible/editable/executable by all agents). A local entry
/// shadows a global one with the same name for this agent; a materialized global appears as a
/// native tool for all agents.
/// </summary>
public static class PocketTool
{
    private const string Prefix = "pocket.";
    private const string NamePattern = "^[a-z0-9_]{1,64}$";
    private const string CategoryPattern = "^[a-z0-9_-]{1,64}$";
    private const string ScopeLocal = "local";
    private const string ScopeGlobal = "global";

    /// <summary>Sentinel agent id: global (shared) pocket entries live in this KV namespace and are visible to all agents.</summary>
    public const string GlobalAgentId = "__global__";

    /// <summary>Suffix appended to the native (materialized) tool name so pocket tools are clearly marked, e.g. "git_st" → "git_st_pocket".</summary>
    private const string NativeSuffix = "_pocket";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

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

    // ── Data model ──────────────────────────────────────────────────────────

    /// <summary>Single argument definition of a pocket entry.</summary>
    public sealed class PocketArg
    {
        public string Name { get; set; } = "";
        public string Desc { get; set; } = "";
        public bool Req { get; set; }
        public string? Def { get; set; }
        public bool Raw { get; set; } // if true, value is inserted without shell quoting
    }

    /// <summary>A pocket tool entry: alias + command template + arg schema.</summary>
    public sealed class PocketEntry
    {
        public string Name { get; set; } = "";
        public string Desc { get; set; } = "";
        public string Cmd { get; set; } = "";
        public string? Cwd { get; set; }
        public string Src { get; set; } = "cmd";
        public string Category { get; set; } = "common";
        public List<PocketArg> Args { get; set; } = [];
        public string Created { get; set; } = "";
        /// <summary>True after the command template passed the add-time safety check (forbidden checks + interactive confirmation).</summary>
        public bool Approved { get; set; }
        /// <summary>If true, this entry (favorite) is materialized as a native typed tool in the agent's toolset.</summary>
        public bool Materialize { get; set; }
    }

    // ── KV helpers ──────────────────────────────────────────────────────────

    public static string NormalizeName(string? name) => (name ?? "").Trim().ToLowerInvariant();

    /// <summary>Native (materialized) name for a pocket entry: entry name + <see cref="NativeSuffix"/>.
    /// Names already ending with the suffix are not double-suffixed. Truncated to keep total length ≤ 64.</summary>
    public static string MaterializedName(string name)
    {
        if (name.EndsWith(NativeSuffix, StringComparison.OrdinalIgnoreCase))
            return name;
        const int maxNameLen = 64;
        if (name.Length + NativeSuffix.Length > maxNameLen)
            name = name[..(maxNameLen - NativeSuffix.Length)];
        return name + NativeSuffix;
    }

    /// <summary>Normalize a category: trim, lowercase, empty → "common".</summary>
    public static string NormalizeCategory(string? category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        return c.Length == 0 ? "common" : c;
    }

    /// <summary>Normalize a scope: trim, lowercase, empty → "local".</summary>
    public static string NormalizeScope(string? scope)
    {
        var s = (scope ?? "").Trim().ToLowerInvariant();
        return s.Length == 0 ? ScopeLocal : s;
    }

    private static bool IsValidScope(string scope) => scope is ScopeLocal or ScopeGlobal;

    private static bool IsValidName(string name) => Regex.IsMatch(name, NamePattern);

    private static bool IsValidCategory(string category) => Regex.IsMatch(category, CategoryPattern);

    private static string KeyFor(string name) => Prefix + name;

    /// <summary>Load all pocket entries for an agent, sorted by name.</summary>
    public static async Task<List<PocketEntry>> LoadEntriesAsync(IKVStore kvStore, string agentId)
    {
        var raw = await kvStore.ListAsync(agentId, Prefix + "*");
        var entries = new List<PocketEntry>();
        foreach (var (_, v) in raw)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<PocketEntry>(v, JsonOpts);
                if (entry is not null && !string.IsNullOrEmpty(entry.Name))
                {
                    entry.Category = NormalizeCategory(entry.Category);
                    entries.Add(entry);
                }
            }
            catch
            {
                // skip corrupt entry
            }
        }
        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Get a single pocket entry by exact name (case-insensitive).</summary>
    public static async Task<PocketEntry?> GetEntryAsync(IKVStore kvStore, string agentId, string name)
    {
        var v = await kvStore.GetAsync(agentId, KeyFor(name));
        if (string.IsNullOrEmpty(v)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<PocketEntry>(v, JsonOpts);
            if (entry is not null) entry.Category = NormalizeCategory(entry.Category);
            return entry;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Load global (shared) pocket entries — visible to all agents — sorted by name.</summary>
    public static Task<List<PocketEntry>> LoadGlobalEntriesAsync(IKVStore kvStore)
        => LoadEntriesAsync(kvStore, GlobalAgentId);

    /// <summary>Get a global (shared) pocket entry by exact name (case-insensitive).</summary>
    public static Task<PocketEntry?> GetGlobalEntryAsync(IKVStore kvStore, string name)
        => GetEntryAsync(kvStore, GlobalAgentId, name);

    /// <summary>Resolve the effective entry for an agent: a local entry shadows the global one with the same name.</summary>
    public static async Task<PocketEntry?> GetEffectiveEntryAsync(IKVStore kvStore, string agentId, string name)
        => await GetEntryAsync(kvStore, agentId, name) ?? await GetGlobalEntryAsync(kvStore, name);

    // ── Execution ───────────────────────────────────────────────────────────

    /// <summary>Executes a pocket entry's command template with the given args via the bash pipeline.</summary>
    public delegate Task<string> PocketRunner(PocketEntry entry, IReadOnlyDictionary<string, object?>? args, CancellationToken ct);

    /// <summary>Create a runner bound to the agent's bash/safety pipeline.</summary>
    public static PocketRunner CreateRunner(
        IBashSessionManager bashManager, IConfigService cfg, IKVStore kvStore,
        bool isSubAgent, ISafetyChecker? safetyChecker, string agentId)
        => async (entry, args, ct) =>
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (args is not null)
            {
                foreach (var kv in args)
                    values[kv.Key] = ArgToString(kv.Value);
            }

            var missing = entry.Args.Where(a => a.Req && !values.ContainsKey(a.Name)).Select(a => a.Name).ToList();
            if (missing.Count > 0)
                return $"Error: missing required arg(s): {string.Join(", ", missing)}";

            var cmd = Substitute(entry.Cmd, values, entry.Args);
            var bashOpts = await cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, agentId);
            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, agentId);
            // Approved entries skip the interactive confirmation at run time (checked once at add);
            // hard block-checks (forbidden commands/directories) still apply.
            return await BashTool.ExecuteBash(
                cmd, entry.Cwd, timeoutMs: null, bashManager, agentId,
                dedupOpts, bashOpts, kvStore, isSubAgent, safetyChecker, ct,
                skipInteractiveConfirm: entry.Approved);
        };

    /// <summary>Replace {arg} placeholders in the command template with values (shell-quoted unless raw).</summary>
    private static string Substitute(string cmd, Dictionary<string, string?> values, List<PocketArg> args)
    {
        foreach (var arg in args)
        {
            var has = values.TryGetValue(arg.Name, out var v);
            var final = has ? (v ?? "") : (arg.Def ?? "");
            var inserted = arg.Raw ? final : ShellQuote(final);
            cmd = Regex.Replace(cmd, @"(?<!\$)\{" + Regex.Escape(arg.Name) + @"\}", inserted, RegexOptions.IgnoreCase);
        }
        return cmd;
    }

    /// <summary>Shell-quote a value: safe-char runs pass through, everything else gets single-quoted.</summary>
    private static string ShellQuote(string value)
    {
        if (value.Length == 0) return "''";
        if (Regex.IsMatch(value, @"^[A-Za-z0-9_\-./:=@%+,]+$")) return value;
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private static string? ArgToString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        JsonElement je => je.ToString(),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Materialized native tool for a pocket entry: name/description/schema come from the entry,
    /// invocation routes through the pocket runner.
    /// </summary>
    public static AIFunction AsPocketFunction(PocketEntry entry, PocketRunner runner, bool isGlobal = false)
        => new PocketFunction(entry, runner, isGlobal);

    public sealed class PocketFunction : AIFunction
    {
        private readonly PocketEntry _entry;
        private readonly PocketRunner _runner;
        private readonly bool _isGlobal;

        private static readonly IReadOnlyDictionary<string, object?> EmptyProps = new Dictionary<string, object?>();

        public PocketFunction(PocketEntry entry, PocketRunner runner, bool isGlobal = false)
        {
            _entry = entry;
            _runner = runner;
            _isGlobal = isGlobal;
        }

        public override string Name => MaterializedName(_entry.Name);
        public override string Description => BuildDescription(_entry, _isGlobal);
        public override JsonElement JsonSchema => BuildSchema(_entry);
        public override MethodInfo? UnderlyingMethod => null;
        public override JsonSerializerOptions JsonSerializerOptions => JsonOpts;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => EmptyProps;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments? args, CancellationToken ct)
            => await _runner(_entry, args, ct);
    }

    // ── Management tools ────────────────────────────────────────────────────

    /// <summary>All five pocket_* management tools (list/add/set/remove/run).</summary>
    public static IReadOnlyList<AIFunction> AsManagementFunctions(
        IKVStore kvStore, IConfigService cfg, IBashSessionManager bashManager,
        SubAgentManager subAgentManager, ISafetyChecker? safetyChecker,
        string agentId, bool isSubAgent)
    {
        var runner = CreateRunner(bashManager, cfg, kvStore, isSubAgent, safetyChecker, agentId);
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

    // ── Handlers ────────────────────────────────────────────────────────────

    private static async Task<string> HandleList(IKVStore kvStore, string agentId, string? pattern, string? category, bool details)
    {
        var local = await LoadEntriesAsync(kvStore, agentId);
        var global = await LoadGlobalEntriesAsync(kvStore);
        var localNames = new HashSet<string>(local.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var items = new List<(PocketEntry Entry, bool Global, bool Shadowed)>();
        items.AddRange(local.Select(e => (e, false, false)));
        items.AddRange(global.Select(g => (g, true, localNames.Contains(g.Name))));

        if (!string.IsNullOrWhiteSpace(pattern) && pattern != "*")
            items = items.Where(t => GlobToRegex(pattern).IsMatch(t.Entry.Name)).ToList();

        if (!string.IsNullOrWhiteSpace(category) && category != "*")
        {
            var cat = NormalizeCategory(category);
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
                sb.AppendLine(FormatEntry(items[i].Entry, items[i].Global, items[i].Shadowed));
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

    private static async Task<string> HandleAdd(
        IConfigService cfg, IKVStore kvStore, string agentId, bool isEphemeral, bool isSubAgent,
        ISafetyChecker? safetyChecker, string name, string desc, string cmd, string? argsJson, string? cwd, string? category,
        bool materialize, string? scope, CancellationToken ct)
    {
        var n = NormalizeName(name);
        if (!IsValidName(n))
            return $"Error: invalid pocket tool name '{name}'. Use lowercase [a-z0-9_], max 64 chars.";
        if (string.IsNullOrWhiteSpace(desc))
            return "Error: 'desc' is required.";
        if (string.IsNullOrWhiteSpace(cmd))
            return "Error: 'cmd' is required.";
        if (isEphemeral)
            return "Error: pocket tools are not persisted for ephemeral agents.";

        var sc = NormalizeScope(scope);
        if (!IsValidScope(sc))
            return $"Error: invalid scope '{scope}'. Use 'local' or 'global'.";

        var cat = NormalizeCategory(category);
        if (!IsValidCategory(cat))
            return $"Error: invalid category '{category}'. Use lowercase [a-z0-9_-], max 64 chars.";

        var storeAgentId = sc == ScopeGlobal ? GlobalAgentId : agentId;
        var existing = await GetEntryAsync(kvStore, storeAgentId, n);
        if (existing is not null)
            return $"Error: {sc} pocket tool '{n}' already exists:\n\n{FormatEntry(existing, sc == ScopeGlobal)}\n\nRemove it first (pocket_remove) or use pocket_set to modify the existing entry (no overwrite on add).";

        var args = ParseArgs(argsJson);
        if (args is null)
            return "Error: 'args' must be a JSON array, e.g. [{\"name\":\"file\",\"desc\":\"scene file\",\"req\":true}].";

        var entry = new PocketEntry
        {
            Name = n,
            Desc = desc.Trim(),
            Cmd = cmd.Trim(),
            Cwd = string.IsNullOrWhiteSpace(cwd) ? null : cwd.Trim(),
            Src = "cmd",
            Category = cat,
            Args = args,
            Created = DateTime.UtcNow.ToString("O"),
            Materialize = materialize,
        };

        var (ok, preflightError) = await PreflightCheckAsync(cfg, kvStore, isSubAgent, safetyChecker, agentId, entry, ct);
        if (!ok)
            return preflightError ?? "Error: command was not approved.";

        entry.Approved = true;

        await kvStore.SetAsync(storeAgentId, KeyFor(n), JsonSerializer.Serialize(entry, JsonOpts));

        var sb = new StringBuilder();
        sb.AppendLine($"✓ Pocket tool '{n}' added ({sc}):");
        sb.AppendLine(FormatEntry(entry, sc == ScopeGlobal));

        var undefined = FindUndefinedPlaceholders(entry);
        if (undefined.Count > 0)
            sb.AppendLine($"\nWarning: placeholder(s) not defined in args: {string.Join(", ", undefined)}");

        if (sc == ScopeLocal)
        {
            if (await GetGlobalEntryAsync(kvStore, n) is not null)
                sb.AppendLine($"\nNote: local '{n}' shadows a global (shared) tool with the same name — local takes precedence for this agent; the global stays intact for others.");
        }
        else
        {
            if (await GetEntryAsync(kvStore, agentId, n) is not null)
                sb.AppendLine($"\nNote: a local '{n}' exists for this agent — it takes precedence (shadows) the new global tool here.");
        }

        if (materialize)
        {
            sb.AppendLine(sc == ScopeGlobal
                ? $"\nFavorite (global) — will appear as native tool '{MaterializedName(n)}' for ALL agents on the next turn."
                : $"\nFavorite — will appear as native tool '{MaterializedName(n)}' on the next turn (same-turn: use pocket_run with '{n}').");
            if (KnownBuiltins.Contains(n))
                sb.AppendLine($"Note: entry name '{n}' matches a builtin tool — no clash, the native tool is suffixed ('{MaterializedName(n)}').");
        }
        else
        {
            sb.AppendLine("\nNot materialized — use pocket_run to execute. To make it a native tool, re-add with materialize=true.");
        }
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> HandleSet(
        IConfigService cfg, IKVStore kvStore, string agentId, bool isEphemeral, bool isSubAgent,
        ISafetyChecker? safetyChecker, string name, string arg, string value, string? scope, CancellationToken ct)
    {
        if (isEphemeral)
            return "Error: pocket tools are not persisted for ephemeral agents.";

        var n = NormalizeName(name);
        var explicitScope = !string.IsNullOrWhiteSpace(scope);
        var sc = NormalizeScope(scope);
        if (!IsValidScope(sc))
            return $"Error: invalid scope '{scope}'. Use 'local' or 'global'.";

        // Resolve the source entry: explicit scope → that scope only; omitted → effective (local first, else global).
        PocketEntry? existing;
        string srcAgentId;
        if (explicitScope)
        {
            srcAgentId = sc == ScopeGlobal ? GlobalAgentId : agentId;
            existing = await GetEntryAsync(kvStore, srcAgentId, n);
            if (existing is null)
                return $"Error: no {sc} pocket tool '{n}' — nothing to set. Use pocket_list to see available tools.";
        }
        else
        {
            existing = await GetEntryAsync(kvStore, agentId, n);
            if (existing is not null)
            {
                srcAgentId = agentId;
            }
            else
            {
                existing = await GetGlobalEntryAsync(kvStore, n);
                if (existing is null)
                    return $"Error: no pocket tool '{n}' — nothing to set. Use pocket_list to see available tools.";
                srcAgentId = GlobalAgentId;
            }
        }
        var currentScope = srcAgentId == GlobalAgentId ? ScopeGlobal : ScopeLocal;

        var field = (arg ?? "").Trim().ToLowerInvariant();
        if (!SettableFields.Contains(field))
            return $"Error: unknown field '{arg}'. Settable: {string.Join(", ", SettableFields.OrderBy(f => f, StringComparer.Ordinal))}.";

        var updated = JsonSerializer.Deserialize<PocketEntry>(JsonSerializer.Serialize(existing, JsonOpts), JsonOpts)!;

        switch (field)
        {
            case "materialize":
                if (!bool.TryParse((value ?? "").Trim(), out var mat))
                    return "Error: 'materialize' value must be 'true' or 'false'.";
                updated.Materialize = mat;
                break;

            case "scope":
                var targetScope = NormalizeScope(value);
                if (!IsValidScope(targetScope))
                    return $"Error: invalid scope '{value}'. Use 'local' or 'global'.";
                if (string.Equals(targetScope, currentScope, StringComparison.Ordinal))
                    return $"✓ Pocket tool '{n}' is already {currentScope} — nothing to change.";
                var targetAgentId = targetScope == ScopeGlobal ? GlobalAgentId : agentId;
                if (await GetEntryAsync(kvStore, targetAgentId, n) is not null)
                    return $"Error: {targetScope} pocket tool '{n}' already exists — move blocked (no overwrite). Remove it first or choose another name.";
                await kvStore.SetAsync(targetAgentId, KeyFor(n), JsonSerializer.Serialize(updated, JsonOpts));
                await kvStore.DeleteAsync(srcAgentId, KeyFor(n));
                var moveSb = new StringBuilder();
                moveSb.AppendLine(currentScope == ScopeLocal
                    ? $"✓ Pocket tool '{n}' moved local → global — now shared: visible and executable by all agents."
                    : $"✓ Pocket tool '{n}' moved global → local — now private to this agent.");
                if (updated.Materialize)
                    moveSb.AppendLine(currentScope == ScopeLocal
                        ? $"Favorite (global) — native tool '{MaterializedName(n)}' will appear for ALL agents on the next turn."
                        : $"Favorite — native tool '{MaterializedName(n)}' will appear for this agent on the next turn.");
                return moveSb.ToString().TrimEnd();

            case "category":
                var cat = NormalizeCategory(value);
                if (!IsValidCategory(cat))
                    return $"Error: invalid category '{value}'. Use lowercase [a-z0-9_-], max 64 chars.";
                updated.Category = cat;
                break;

            case "desc":
                var desc = (value ?? "").Trim();
                if (desc.Length == 0)
                    return "Error: 'desc' cannot be empty.";
                updated.Desc = desc;
                break;

            case "cmd":
                var cmd = (value ?? "").Trim();
                if (cmd.Length == 0)
                    return "Error: 'cmd' cannot be empty.";
                updated.Cmd = cmd;
                updated.Approved = false;
                var (ok, preflightError) = await PreflightCheckAsync(cfg, kvStore, isSubAgent, safetyChecker, agentId, updated, ct);
                if (!ok)
                    return preflightError ?? "Error: command was not approved.";
                updated.Approved = true;
                break;

            case "cwd":
                updated.Cwd = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                break;

            case "args":
                var args = ParseArgs(value);
                if (args is null)
                    return "Error: 'args' must be a JSON array, e.g. [{\"name\":\"file\",\"desc\":\"scene file\",\"req\":true}].";
                updated.Args = args;
                break;

            case "name":
                var newName = NormalizeName(value);
                if (!IsValidName(newName))
                    return $"Error: invalid pocket tool name '{value}'. Use lowercase [a-z0-9_], max 64 chars.";
                if (string.Equals(newName, n, StringComparison.OrdinalIgnoreCase))
                    return $"✓ Pocket tool '{n}' — name is already '{newName}', nothing to change.";
                if (await GetEntryAsync(kvStore, srcAgentId, newName) is not null)
                    return $"Error: {currentScope} pocket tool '{newName}' already exists — rename blocked (no overwrite). Remove it first or choose another name.";
                updated.Name = newName;
                await kvStore.SetAsync(srcAgentId, KeyFor(newName), JsonSerializer.Serialize(updated, JsonOpts));
                await kvStore.DeleteAsync(srcAgentId, KeyFor(n));
                var renameSb = new StringBuilder();
                renameSb.AppendLine($"✓ Pocket tool '{n}' renamed to '{newName}' ({currentScope}; native name '{MaterializedName(newName)}').");
                if (KnownBuiltins.Contains(newName))
                    renameSb.AppendLine($"Note: entry name '{newName}' matches a builtin tool — no clash, the native tool is suffixed ('{MaterializedName(newName)}').");
                return renameSb.ToString().TrimEnd();
        }

        await kvStore.SetAsync(srcAgentId, KeyFor(n), JsonSerializer.Serialize(updated, JsonOpts));

        var sb = new StringBuilder();
        sb.AppendLine($"✓ Pocket tool '{n}' updated ({field}, {currentScope}):");
        sb.AppendLine(FormatEntry(updated, currentScope == ScopeGlobal));

        if (field == "materialize")
        {
            sb.AppendLine(updated.Materialize
                ? currentScope == ScopeGlobal
                    ? $"Favorite (global) — will appear as native tool '{MaterializedName(updated.Name)}' for ALL agents on the next turn."
                    : $"Favorite — will appear as native tool '{MaterializedName(updated.Name)}' on the next turn (same-turn: use pocket_run with '{updated.Name}')."
                : $"No longer materialized — '{updated.Name}' is pocket-only; native tool '{MaterializedName(updated.Name)}' disappears on the next turn.");
        }

        var undefined = FindUndefinedPlaceholders(updated);
        if (undefined.Count > 0)
            sb.AppendLine($"\nWarning: placeholder(s) not defined in args: {string.Join(", ", undefined)}");

        return sb.ToString().TrimEnd();
    }

    private static async Task<string> HandleRemove(IKVStore kvStore, string agentId, bool isEphemeral, string name, string? scope)
    {
        if (isEphemeral)
            return "Error: pocket tools are not persisted for ephemeral agents.";

        var n = NormalizeName(name);
        var explicitScope = !string.IsNullOrWhiteSpace(scope);
        var sc = NormalizeScope(scope);
        if (!IsValidScope(sc))
            return $"Error: invalid scope '{scope}'. Use 'local' or 'global'.";

        if (explicitScope)
        {
            var targetAgentId = sc == ScopeGlobal ? GlobalAgentId : agentId;
            var existing = await GetEntryAsync(kvStore, targetAgentId, n);
            if (existing is null)
                return $"Error: no {sc} pocket tool '{n}' — nothing to remove. Use pocket_list to see available tools.";
            await kvStore.DeleteAsync(targetAgentId, KeyFor(n));
            return sc == ScopeGlobal
                ? $"✓ Global pocket tool '{n}' removed (shared — gone for all agents)."
                : $"✓ Pocket tool '{n}' removed.";
        }

        var local = await GetEntryAsync(kvStore, agentId, n);
        if (local is not null)
        {
            await kvStore.DeleteAsync(agentId, KeyFor(n));
            return $"✓ Pocket tool '{n}' removed (local shadow — the global tool, if any, stays for others).";
        }

        var global = await GetGlobalEntryAsync(kvStore, n);
        if (global is not null)
        {
            await kvStore.DeleteAsync(GlobalAgentId, KeyFor(n));
            return $"✓ Global pocket tool '{n}' removed (shared — gone for all agents).";
        }

        return $"Error: no pocket tool '{n}' — nothing to remove. Use pocket_list to see available tools.";
    }

    /// <summary>
    /// One-time safety pre-flight for a command template at add time: forbidden-command/directory
    /// hard blocks, then an interactive confirmation (OK/Stop/Check) when the template matches a
    /// CheckRequireCommands pattern, with an optional AI safety verdict for the Check choice.
    /// Mirrors the runtime checks in <see cref="BashTool.ExecuteBash"/>.
    /// </summary>
    private static async Task<(bool Ok, string? Error)> PreflightCheckAsync(
        IConfigService cfg, IKVStore kvStore, bool isSubAgent, ISafetyChecker? safetyChecker,
        string agentId, PocketEntry entry, CancellationToken ct)
    {
        var bashOpts = await cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, agentId);
        var trimmed = entry.Cmd.Trim();

        foreach (var forbidden in bashOpts.ForbiddenCommands)
        {
            if (string.IsNullOrEmpty(forbidden)) continue;
            if (trimmed.Equals(forbidden, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(forbidden + " ", StringComparison.OrdinalIgnoreCase))
                return (false, $"Error: command blocked — '{entry.Cmd}' matches forbidden pattern '{forbidden}'. Try an alternative approach.");
        }

        if (!string.IsNullOrEmpty(entry.Cwd))
        {
            var normalizedWorkdir = entry.Cwd.Replace('\\', '/').TrimEnd('/');
            foreach (var forbiddenDir in bashOpts.ForbiddenDirectories)
            {
                if (string.IsNullOrEmpty(forbiddenDir)) continue;
                var normalizedForbidden = forbiddenDir.Replace('\\', '/').TrimEnd('/');
                if (normalizedWorkdir.Equals(normalizedForbidden, StringComparison.OrdinalIgnoreCase) ||
                    normalizedWorkdir.StartsWith(normalizedForbidden + "/", StringComparison.OrdinalIgnoreCase))
                    return (false, $"Error: directory blocked — '{entry.Cwd}' matches blocked path '{forbiddenDir}'. Try an alternative approach.");
            }
        }

        foreach (var check in bashOpts.CheckRequireCommands)
        {
            if (string.IsNullOrEmpty(check)) continue;
            if (trimmed.Equals(check, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(check + " ", StringComparison.OrdinalIgnoreCase))
            {
                var choice = await InteractiveConfirmation.ShowAsync(
                    trimmed, bashOpts.CheckRequireCommandsTimeout * 1000, kvStore, agentId, isSubAgent, ct);

                switch (choice)
                {
                    case InteractiveConfirmation.Choice.Stop:
                        return (false, $"Error: command blocked by user — '{entry.Cmd}' requires explicit approval. Choose a different command or ask the user.");
                    case InteractiveConfirmation.Choice.Check:
                        if (safetyChecker is not null)
                        {
                            var verdict = await safetyChecker.CheckAsync(agentId, trimmed, ct);
                            if (!verdict.Allow)
                                return (false, $"Error: command blocked by safety check — '{entry.Cmd}' — {verdict.Why}");
                        }
                        break;
                    case InteractiveConfirmation.Choice.Ok:
                    default:
                        break;
                }
                break; // confirmed — no need to check remaining patterns
            }
        }

        return (true, null);
    }

    private static async Task<string> HandleRun(
        IKVStore kvStore, PocketRunner runner, string agentId, string tool, string? argsJson, CancellationToken ct)
    {
        var n = NormalizeName(tool);
        var entry = await GetEffectiveEntryAsync(kvStore, agentId, n);
        if (entry is null)
            return $"No pocket tool '{tool}'. Use pocket_list to see available tools.";

        var args = ParseArgsObject(argsJson);
        if (args is null)
            return "Error: 'args' must be a JSON object, e.g. {\"file\":\"scene.py\"}.";

        return await runner(entry, args, ct);
    }

    // ── Parsing & formatting ────────────────────────────────────────────────

    private static List<PocketArg>? ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<PocketArg>>(json, JsonOpts) ?? [];
            foreach (var a in list)
            {
                a.Name = (a.Name ?? "").Trim().ToLowerInvariant();
                if (!IsValidName(a.Name)) return null;
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?>? ParseArgsObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> FindUndefinedPlaceholders(PocketEntry entry)
    {
        var defined = new HashSet<string>(entry.Args.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        return Regex.Matches(entry.Cmd, @"(?<!\$)\{([a-z0-9_]+)\}", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Where(p => !defined.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string FormatEntry(PocketEntry e, bool global = false, bool shadowed = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  Name:    {e.Name} (src={e.Src})");
        sb.AppendLine($"  Cat:     {e.Category}");
        sb.AppendLine($"  Scope:   {(global ? "global" : "local")}");
        if (shadowed)
            sb.AppendLine("  Shadowed: yes — a local entry with the same name takes precedence for this agent");
        sb.AppendLine(e.Approved
            ? "  Safety:  approved at add"
            : "  Safety:  legacy — checked on each run");
        sb.AppendLine(e.Materialize
            ? $"  Native:  yes — '{MaterializedName(e.Name)}'"
            : "  Native:  no — pocket only");
        sb.AppendLine($"  Desc:    {e.Desc}");
        sb.AppendLine($"  Cmd:     {e.Cmd}");
        if (!string.IsNullOrEmpty(e.Cwd)) sb.AppendLine($"  Cwd:     {e.Cwd}");

        if (e.Args.Count == 0)
        {
            sb.AppendLine("  Args:    (none)");
        }
        else
        {
            sb.AppendLine("  Args:");
            foreach (var a in e.Args)
            {
                var req = a.Req ? "required" : "optional";
                var def = a.Def is null ? "" : $", default={a.Def}";
                var raw = a.Raw ? ", raw" : "";
                var desc = string.IsNullOrEmpty(a.Desc) ? "" : $" — {a.Desc}";
                sb.AppendLine($"    {a.Name} ({req}{def}{raw}){desc}");
            }
        }

        if (!string.IsNullOrEmpty(e.Created)) sb.AppendLine($"  Created: {e.Created}");
        return sb.ToString().TrimEnd();
    }

    private static string BuildDescription(PocketEntry e, bool global = false)
    {
        var sb = new StringBuilder(e.Desc);
        if (e.Args.Count > 0)
        {
            sb.Append("\nArgs: ");
            sb.Append(string.Join(", ", e.Args.Select(a => a.Req ? $"{a.Name} (required)" : a.Name)));
        }
        sb.Append($"\nRuns: {e.Cmd}");
        if (e.Materialize)
            sb.Append(global
                ? $"\n(Pocket tool — GLOBAL shared tool; native name '{MaterializedName(e.Name)}'; also run via pocket_run as '{e.Name}')"
                : $"\n(Pocket tool — native name '{MaterializedName(e.Name)}'; also run via pocket_run as '{e.Name}')");
        return sb.ToString();
    }

    private static JsonElement BuildSchema(PocketEntry entry)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");

            w.WritePropertyName("properties");
            w.WriteStartObject();
            foreach (var a in entry.Args)
            {
                w.WritePropertyName(a.Name);
                w.WriteStartObject();
                w.WriteString("type", "string");
                if (!string.IsNullOrEmpty(a.Desc)) w.WriteString("description", a.Desc);
                if (a.Def is not null) w.WriteString("default", a.Def);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            var required = entry.Args.Where(a => a.Req).Select(a => a.Name).ToList();
            if (required.Count > 0)
            {
                w.WritePropertyName("required");
                w.WriteStartArray();
                foreach (var r in required) w.WriteStringValue(r);
                w.WriteEndArray();
            }

            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }
}
