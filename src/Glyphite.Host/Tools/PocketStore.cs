using System.Text.Json;
using Glyphite.Abstractions.Interfaces;

namespace Glyphite.Host.Tools;

/// <summary>
/// The KV vault behind pocket tools: one JSON value per entry under <c>pocket.&lt;name&gt;</c>.
/// Local entries live in the agent's own namespace, global (shared) ones under
/// <see cref="GlobalAgentId"/>; a local entry shadows a global one with the same name.
///
/// This is the only place that knows the key layout and the stored JSON shape — handlers go through
/// <see cref="SaveAsync"/>/<see cref="DeleteAsync"/> so serialization can never drift between sites.
/// </summary>
public static class PocketStore
{
    private const string Prefix = "pocket.";

    public const string ScopeLocal = "local";
    public const string ScopeGlobal = "global";

    /// <summary>Sentinel agent id: global (shared) pocket entries live in this KV namespace and are visible to all agents.</summary>
    public const string GlobalAgentId = "__global__";

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Normalize a scope: trim, lowercase, empty → "local".</summary>
    public static string NormalizeScope(string? scope)
    {
        var s = (scope ?? "").Trim().ToLowerInvariant();
        return s.Length == 0 ? ScopeLocal : s;
    }

    public static bool IsValidScope(string scope) => scope is ScopeLocal or ScopeGlobal;

    /// <summary>Namespace an entry lives in: global entries share <see cref="GlobalAgentId"/>.</summary>
    public static string NamespaceFor(string scope, string agentId)
        => scope == ScopeGlobal ? GlobalAgentId : agentId;

    internal static string KeyFor(string name) => Prefix + name;

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
                    entry.Category = PocketEntry.NormalizeCategory(entry.Category);
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
            if (entry is not null) entry.Category = PocketEntry.NormalizeCategory(entry.Category);
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

    /// <summary>Write an entry under its own name in the given namespace (agent id or <see cref="GlobalAgentId"/>).</summary>
    public static Task SaveAsync(IKVStore kvStore, string agentId, PocketEntry entry)
        => kvStore.SetAsync(agentId, KeyFor(entry.Name), JsonSerializer.Serialize(entry, JsonOpts));

    /// <summary>Delete an entry by name from the given namespace.</summary>
    public static Task DeleteAsync(IKVStore kvStore, string agentId, string name)
        => kvStore.DeleteAsync(agentId, KeyFor(name));
}
