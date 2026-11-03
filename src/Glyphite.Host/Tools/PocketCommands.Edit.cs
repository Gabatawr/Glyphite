using System.Text;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Utils;

namespace Glyphite.Host.Tools;

/// <summary>
/// The editing half of the pocket tools: creating an entry, changing one field of it, and the
/// add-time safety pre-flight both go through.
/// </summary>
public static partial class PocketCommands
{
    private static async Task<string> HandleAdd(
        IConfigService cfg, IKVStore kvStore, string agentId, bool isEphemeral, bool isSubAgent,
        ISafetyChecker? safetyChecker, string name, string desc, string cmd, string? argsJson, string? cwd, string? category,
        bool materialize, string? scope, CancellationToken ct)
    {
        var n = PocketEntry.NormalizeName(name);
        if (!PocketEntry.IsValidName(n))
            return $"Error: invalid pocket tool name '{name}'. Use lowercase [a-z0-9_], max 64 chars.";
        if (string.IsNullOrWhiteSpace(desc))
            return "Error: 'desc' is required.";
        if (string.IsNullOrWhiteSpace(cmd))
            return "Error: 'cmd' is required.";
        if (isEphemeral)
            return "Error: pocket tools are not persisted for ephemeral agents.";

        var sc = PocketStore.NormalizeScope(scope);
        if (!PocketStore.IsValidScope(sc))
            return $"Error: invalid scope '{scope}'. Use 'local' or 'global'.";

        var cat = PocketEntry.NormalizeCategory(category);
        if (!PocketEntry.IsValidCategory(cat))
            return $"Error: invalid category '{category}'. Use lowercase [a-z0-9_-], max 64 chars.";

        var storeAgentId = PocketStore.NamespaceFor(sc, agentId);
        var existing = await PocketStore.GetEntryAsync(kvStore, storeAgentId, n);
        if (existing is not null)
            return $"Error: {sc} pocket tool '{n}' already exists:\n\n{PocketSchema.FormatEntry(existing, sc == PocketStore.ScopeGlobal)}\n\nRemove it first (pocket_remove) or use pocket_set to modify the existing entry (no overwrite on add).";

        var args = PocketSchema.ParseArgs(argsJson);
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

        await PocketStore.SaveAsync(kvStore, storeAgentId, entry);

        var sb = new StringBuilder();
        sb.AppendLine($"✓ Pocket tool '{n}' added ({sc}):");
        sb.AppendLine(PocketSchema.FormatEntry(entry, sc == PocketStore.ScopeGlobal));

        var undefined = PocketSchema.FindUndefinedPlaceholders(entry);
        if (undefined.Count > 0)
            sb.AppendLine($"\nWarning: placeholder(s) not defined in args: {string.Join(", ", undefined)}");

        if (sc == PocketStore.ScopeLocal)
        {
            if (await PocketStore.GetGlobalEntryAsync(kvStore, n) is not null)
                sb.AppendLine($"\nNote: local '{n}' shadows a global (shared) tool with the same name — local takes precedence for this agent; the global stays intact for others.");
        }
        else
        {
            if (await PocketStore.GetEntryAsync(kvStore, agentId, n) is not null)
                sb.AppendLine($"\nNote: a local '{n}' exists for this agent — it takes precedence (shadows) the new global tool here.");
        }

        if (materialize)
        {
            sb.AppendLine(sc == PocketStore.ScopeGlobal
                ? $"\nFavorite (global) — will appear as native tool '{PocketEntry.MaterializedName(n)}' for ALL agents on the next turn."
                : $"\nFavorite — will appear as native tool '{PocketEntry.MaterializedName(n)}' on the next turn (same-turn: use pocket_run with '{n}').");
            if (KnownBuiltins.Contains(n))
                sb.AppendLine($"Note: entry name '{n}' matches a builtin tool — no clash, the native tool is suffixed ('{PocketEntry.MaterializedName(n)}').");
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

        var n = PocketEntry.NormalizeName(name);
        var explicitScope = !string.IsNullOrWhiteSpace(scope);
        var sc = PocketStore.NormalizeScope(scope);
        if (!PocketStore.IsValidScope(sc))
            return $"Error: invalid scope '{scope}'. Use 'local' or 'global'.";

        // Resolve the source entry: explicit scope → that scope only; omitted → effective (local first, else global).
        PocketEntry? existing;
        string srcAgentId;
        if (explicitScope)
        {
            srcAgentId = PocketStore.NamespaceFor(sc, agentId);
            existing = await PocketStore.GetEntryAsync(kvStore, srcAgentId, n);
            if (existing is null)
                return $"Error: no {sc} pocket tool '{n}' — nothing to set. Use pocket_list to see available tools.";
        }
        else
        {
            existing = await PocketStore.GetEntryAsync(kvStore, agentId, n);
            if (existing is not null)
            {
                srcAgentId = agentId;
            }
            else
            {
                existing = await PocketStore.GetGlobalEntryAsync(kvStore, n);
                if (existing is null)
                    return $"Error: no pocket tool '{n}' — nothing to set. Use pocket_list to see available tools.";
                srcAgentId = PocketStore.GlobalAgentId;
            }
        }
        var currentScope = srcAgentId == PocketStore.GlobalAgentId ? PocketStore.ScopeGlobal : PocketStore.ScopeLocal;

        var field = (arg ?? "").Trim().ToLowerInvariant();
        if (!SettableFields.Contains(field))
            return $"Error: unknown field '{arg}'. Settable: {string.Join(", ", SettableFields.OrderBy(f => f, StringComparer.Ordinal))}.";

        var updated = JsonSerializer.Deserialize<PocketEntry>(JsonSerializer.Serialize(existing, PocketStore.JsonOpts), PocketStore.JsonOpts)!;

        switch (field)
        {
            case "materialize":
                if (!bool.TryParse((value ?? "").Trim(), out var mat))
                    return "Error: 'materialize' value must be 'true' or 'false'.";
                updated.Materialize = mat;
                break;

            case "scope":
                var targetScope = PocketStore.NormalizeScope(value);
                if (!PocketStore.IsValidScope(targetScope))
                    return $"Error: invalid scope '{value}'. Use 'local' or 'global'.";
                if (string.Equals(targetScope, currentScope, StringComparison.Ordinal))
                    return $"✓ Pocket tool '{n}' is already {currentScope} — nothing to change.";
                var targetAgentId = PocketStore.NamespaceFor(targetScope, agentId);
                if (await PocketStore.GetEntryAsync(kvStore, targetAgentId, n) is not null)
                    return $"Error: {targetScope} pocket tool '{n}' already exists — move blocked (no overwrite). Remove it first or choose another name.";
                await PocketStore.SaveAsync(kvStore, targetAgentId, updated);
                await PocketStore.DeleteAsync(kvStore, srcAgentId, n);
                var moveSb = new StringBuilder();
                moveSb.AppendLine(currentScope == PocketStore.ScopeLocal
                    ? $"✓ Pocket tool '{n}' moved local → global — now shared: visible and executable by all agents."
                    : $"✓ Pocket tool '{n}' moved global → local — now private to this agent.");
                if (updated.Materialize)
                    moveSb.AppendLine(currentScope == PocketStore.ScopeLocal
                        ? $"Favorite (global) — native tool '{PocketEntry.MaterializedName(n)}' will appear for ALL agents on the next turn."
                        : $"Favorite — native tool '{PocketEntry.MaterializedName(n)}' will appear for this agent on the next turn.");
                return moveSb.ToString().TrimEnd();

            case "category":
                var cat = PocketEntry.NormalizeCategory(value);
                if (!PocketEntry.IsValidCategory(cat))
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
                var args = PocketSchema.ParseArgs(value);
                if (args is null)
                    return "Error: 'args' must be a JSON array, e.g. [{\"name\":\"file\",\"desc\":\"scene file\",\"req\":true}].";
                updated.Args = args;
                break;

            case "name":
                var newName = PocketEntry.NormalizeName(value);
                if (!PocketEntry.IsValidName(newName))
                    return $"Error: invalid pocket tool name '{value}'. Use lowercase [a-z0-9_], max 64 chars.";
                if (string.Equals(newName, n, StringComparison.OrdinalIgnoreCase))
                    return $"✓ Pocket tool '{n}' — name is already '{newName}', nothing to change.";
                if (await PocketStore.GetEntryAsync(kvStore, srcAgentId, newName) is not null)
                    return $"Error: {currentScope} pocket tool '{newName}' already exists — rename blocked (no overwrite). Remove it first or choose another name.";
                updated.Name = newName;
                await PocketStore.SaveAsync(kvStore, srcAgentId, updated);
                await PocketStore.DeleteAsync(kvStore, srcAgentId, n);
                var renameSb = new StringBuilder();
                renameSb.AppendLine($"✓ Pocket tool '{n}' renamed to '{newName}' ({currentScope}; native name '{PocketEntry.MaterializedName(newName)}').");
                if (KnownBuiltins.Contains(newName))
                    renameSb.AppendLine($"Note: entry name '{newName}' matches a builtin tool — no clash, the native tool is suffixed ('{PocketEntry.MaterializedName(newName)}').");
                return renameSb.ToString().TrimEnd();
        }

        await PocketStore.SaveAsync(kvStore, srcAgentId, updated);

        var sb = new StringBuilder();
        sb.AppendLine($"✓ Pocket tool '{n}' updated ({field}, {currentScope}):");
        sb.AppendLine(PocketSchema.FormatEntry(updated, currentScope == PocketStore.ScopeGlobal));

        if (field == "materialize")
        {
            sb.AppendLine(updated.Materialize
                ? currentScope == PocketStore.ScopeGlobal
                    ? $"Favorite (global) — will appear as native tool '{PocketEntry.MaterializedName(updated.Name)}' for ALL agents on the next turn."
                    : $"Favorite — will appear as native tool '{PocketEntry.MaterializedName(updated.Name)}' on the next turn (same-turn: use pocket_run with '{updated.Name}')."
                : $"No longer materialized — '{updated.Name}' is pocket-only; native tool '{PocketEntry.MaterializedName(updated.Name)}' disappears on the next turn.");
        }

        var undefined = PocketSchema.FindUndefinedPlaceholders(updated);
        if (undefined.Count > 0)
            sb.AppendLine($"\nWarning: placeholder(s) not defined in args: {string.Join(", ", undefined)}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// One-time safety pre-flight for a command template at add time: forbidden-command/directory
    /// hard blocks, then an interactive confirmation (OK/Stop/Check) when the template matches a
    /// CheckRequireCommands pattern, with an optional AI safety verdict for the Check choice.
    ///
    /// Detection goes through <see cref="CommandAnalyzer"/> — the same reader the runtime uses — so a
    /// template is judged by what it actually runs (<c>cd x &amp;&amp; rm -rf y</c>), not by its first
    /// word. And the prompt is asked once, however many patterns matched.
    /// </summary>
    private static async Task<(bool Ok, string? Error)> PreflightCheckAsync(
        IConfigService cfg, IKVStore kvStore, bool isSubAgent, ISafetyChecker? safetyChecker,
        string agentId, PocketEntry entry, CancellationToken ct)
    {
        var bashOpts = await cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, agentId);
        var trimmed = entry.Cmd.Trim();

        if (CommandAnalyzer.FindMatch(trimmed, bashOpts.ForbiddenCommands) is { } forbidden)
            return (false, $"Error: command blocked — '{entry.Cmd}' matches forbidden pattern '{forbidden}'. Try an alternative approach.");

        if (!string.IsNullOrEmpty(entry.Cwd) &&
            PathGuard.BlockReason(entry.Cwd, bashOpts.ForbiddenDirectories, bashOpts.DefaultDirectory) is { } reason)
            return (false, $"Error: directory blocked — '{entry.Cwd}' {reason}. Try an alternative approach.");

        if (CommandAnalyzer.FindMatch(trimmed, bashOpts.CheckRequireCommands) is not null)
        {
            var timeoutMs = bashOpts.CheckRequireCommandsTimeout * 1000;

            var choice = await InteractiveConfirmation.ShowAsync(
                trimmed, timeoutMs, kvStore, agentId, isSubAgent, ct);

            switch (choice)
            {
                case InteractiveConfirmation.Choice.Stop:
                    return (false, $"Error: command blocked by user — '{entry.Cmd}' requires explicit approval. Choose a different command or ask the user.");

                case InteractiveConfirmation.Choice.Check:
                    var verdict = safetyChecker is not null
                        ? await safetyChecker.CheckAsync(agentId, trimmed, ct)
                        : SafetyVerdict.Unavailable("no safety checker configured");

                    if (verdict.Inconclusive)
                    {
                        // Nothing has judged this template yet, and the entry is about to become
                        // pre-approved for every future run — so the human, not a timer, decides.
                        var fallback = await InteractiveConfirmation.ShowAsync(
                            trimmed, timeoutMs, kvStore, agentId, isSubAgent, ct,
                            noAutoSelect: true, note: "safety check unavailable");

                        if (fallback != InteractiveConfirmation.Choice.Ok)
                            return (false, $"Error: safety check unavailable ({verdict.Why}) and no approval — '{entry.Cmd}' was not saved.");

                        break;
                    }

                    if (!verdict.Allow)
                        return (false, $"Error: command blocked by safety check — '{entry.Cmd}' — {verdict.Why}");
                    break;

                case InteractiveConfirmation.Choice.Ok:
                default:
                    break;
            }
        }

        return (true, null);
    }
}
