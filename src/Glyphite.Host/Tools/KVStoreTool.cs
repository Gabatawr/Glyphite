using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class KVStoreTool
{
    /// <summary>Tracks a pending masked-set operation awaiting accept confirmation.</summary>
    private static readonly ConcurrentDictionary<string, PendingKVOperation?> PendingOps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>In-memory vault overlay for ephemeral agents (subagent_run). Not persisted to DB.</summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> VaultOverlays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clear in-memory vault overlay for an ephemeral agent (called when scope is removed).</summary>
    public static void ClearEphemeralVault(string agentId)
    {
        VaultOverlays.TryRemove(agentId, out _);
    }

    /// <summary>Get vault entries merging DB + ephemeral overlay. Strips empty-value keys.</summary>
    private static async Task<Dictionary<string, string>> GetVaultEntries(string agentId, string? key, IKVStore kvStore)
    {
        var db = await kvStore.ListAsync(agentId, key);

        // Remove empty-value keys (treated as deleted)
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in db)
            if (v.Length > 0)
                clean[k] = v;

        // Append non-empty ephemeral overlay on top
        if (VaultOverlays.TryGetValue(agentId, out var overlay) && overlay.Count > 0)
        {
            var filtered = string.IsNullOrEmpty(key) || key == "*"
                ? overlay
                : FilterByKey(overlay, key);

            foreach (var (k, v) in filtered)
                if (v.Length > 0)
                    clean[k] = v;
                else
                    clean.Remove(k); // overlay marks key as deleted
        }

        return clean;
    }

    public static async Task<string> Execute(
        string action,
        string? key,
        string? value,
        int? ttl,
        string scope,
        IKVStore kvStore,
        IConfigService configService,
        SubAgentManager subAgentManager,
        string agentId)
    {
        var isVault = string.Equals(scope, "vault", StringComparison.OrdinalIgnoreCase);

        switch (action.ToLowerInvariant())
        {
            case "get":
                return await HandleGet(key, isVault, kvStore, configService, agentId);

            case "set":
                return await HandleSet(key, value, ttl, isVault, kvStore, configService, subAgentManager, agentId);

            case "accept":
                return await HandleAccept(kvStore, configService, subAgentManager, agentId);

            default:
                return $"Unknown action '{action}'. Use 'get', 'set', or 'accept'.";
        }
    }

    private static async Task<string> HandleGet(
        string? key,
        bool isVault,
        IKVStore kvStore,
        IConfigService configService,
        string agentId)
    {
        // A get without mask clears pending accept
        if (string.IsNullOrEmpty(key) || !HasWildcard(key))
            ClearPending(agentId);

        Dictionary<string, string> entries;

        if (isVault)
        {
            entries = await GetVaultEntries(agentId, key, kvStore);
        }
        else
        {
            var config = await configService.GetConfigAsync(agentId);
            entries = FilterByKey(config, key);
        }

        if (entries.Count == 0)
            return string.IsNullOrEmpty(key)
                ? "(empty — no keys stored)"
                : $"(no keys matching '{key}')";

        return FormatEntries(entries);
    }

    private static async Task<string> HandleSet(
        string? key,
        string? value,
        int? ttl,
        bool isVault,
        IKVStore kvStore,
        IConfigService configService,
        SubAgentManager subAgentManager,
        string agentId)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "error: 'key' is required for set";

        var hasWildcard = HasWildcard(key);

        // A set without mask clears pending accept
        if (!hasWildcard)
            ClearPending(agentId);

        // Validate config scope doesn't support TTL
        if (!isVault && ttl.HasValue)
            return "error: TTL is only supported for 'vault' scope, not for 'config'";

        if (hasWildcard)
        {
            // Dry-run: show matching keys first
            Dictionary<string, string> matching;

            if (isVault)
            {
                matching = await GetVaultEntries(agentId, key, kvStore);
            }
            else
            {
                var config = await configService.GetConfigAsync(agentId);
                matching = FilterByKey(config, key);
            }

            if (matching.Count == 0)
                return $"(no keys matching '{key}' — nothing to update)";

            var sb = new StringBuilder();
            sb.AppendLine($"── Dry-run: keys matching '{key}' ──────────────────");
            sb.AppendLine(FormatEntries(matching));
            sb.AppendLine($"──────────────────────────────────────────────────");
            sb.AppendLine($"Confirm with action='accept' to set value on {matching.Count} key(s).");

            // Store pending operation (also remember ephemeral status for accept)
            PendingOps[agentId] = new PendingKVOperation(
                isVault, key, value, ttl, matching.Keys.ToList())
            {
                Ephemeral = subAgentManager.IsEphemeral(agentId)
            };

            return sb.ToString().TrimEnd();
        }

        // Direct set (no wildcard)
        var isEphemeral = subAgentManager.IsEphemeral(agentId);

        var normalizedValue = value ?? "";

        if (isVault)
        {
            if (isEphemeral)
            {
                var overlay = VaultOverlays.GetOrAdd(agentId, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                if (normalizedValue.Length == 0)
                    overlay.Remove(key!);
                else
                    overlay[key!] = normalizedValue;
                var ttlInfo = ttl.HasValue ? $" (ttl={ttl}s)" : "";
                var action = normalizedValue.Length == 0 ? "deleted" : $"'{normalizedValue}'";
                return $"set: '{key}' = {action}{ttlInfo} (ephemeral)";
            }

            await kvStore.SetAsync(agentId, key!, normalizedValue, ttl);
            var ttlInfo2 = ttl.HasValue ? $" (ttl={ttl}s)" : "";
            var action2 = normalizedValue.Length == 0 ? "deleted" : $"'{normalizedValue}'";
            return $"set: '{key}' = {action2}{ttlInfo2}";
        }
        else
        {
            if (isEphemeral)
            {
                var config = await configService.GetConfigAsync(agentId);
                if (normalizedValue.Length == 0)
                    config.Remove(key!);
                else
                    config[key!] = normalizedValue;
                configService.SetSessionOverlay(agentId, config);
                var action = normalizedValue.Length == 0 ? "deleted" : $"'{normalizedValue}'";
                return $"set: config '{key}' = {action} (ephemeral)";
            }

            if (normalizedValue.Length == 0)
            {
                await configService.DeleteConfigAsync([key!], scope: "session", agentId: agentId);
                return $"set: config '{key}' = deleted";
            }

            await configService.UpdateConfigAsync(
                new Dictionary<string, string> { [key!] = normalizedValue },
                scope: "session",
                agentId: agentId);
            return $"set: config '{key}' = '{normalizedValue}'";
        }
    }

    private static async Task<string> HandleAccept(
        IKVStore kvStore,
        IConfigService configService,
        SubAgentManager subAgentManager,
        string agentId)
    {
        if (!PendingOps.TryGetValue(agentId, out var pending) || pending is null)
            return "Nothing to accept — no pending masked-set operation. Use 'set' with a mask (wildcard) first.";

        // Clear pending before executing (to avoid double-execution on error)
        PendingOps.TryRemove(agentId, out _);

        if (pending.Keys.Count == 0)
            return "Nothing to accept — no keys matched the pattern.";

        var value = pending.Value ?? "";
        int updated = 0;

        if (pending.IsVault)
        {
            if (pending.Ephemeral)
            {
                var overlay = VaultOverlays.GetOrAdd(agentId, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                foreach (var k in pending.Keys)
                {
                    if (value.Length == 0)
                        overlay.Remove(k);
                    else
                        overlay[k] = value;
                    updated++;
                }
            }
            else
            {
                foreach (var k in pending.Keys)
                {
                    await kvStore.SetAsync(agentId, k, value, pending.Ttl);
                    updated++;
                }
            }
        }
        else
        {
            var changes = pending.Keys.ToDictionary(k => k, k => value, StringComparer.OrdinalIgnoreCase);

            if (pending.Ephemeral)
            {
                var config = await configService.GetConfigAsync(agentId);
                foreach (var (k, v) in changes)
                {
                    if (v.Length == 0)
                        config.Remove(k);
                    else
                        config[k] = v;
                }
                configService.SetSessionOverlay(agentId, config);
                updated = changes.Count;
            }
            else
            {
                var keysToDelete = changes.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToArray();
                var keysToUpdate = changes.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                if (keysToDelete.Length > 0)
                    await configService.DeleteConfigAsync(keysToDelete, scope: "session", agentId: agentId);

                if (keysToUpdate.Count > 0)
                {
                    var result = await configService.UpdateConfigAsync(keysToUpdate, scope: "session", agentId: agentId);
                    updated = result.Updated.Count + keysToDelete.Length;
                }
                else
                {
                    updated = keysToDelete.Length;
                }
            }
        }

        return $"accept: set value on {updated} key(s): {string.Join(", ", pending.Keys)}";
    }

    private static bool HasWildcard(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return key.Contains('*') || key.Contains('?');
    }

    private static void ClearPending(string agentId)
    {
        PendingOps.TryRemove(agentId, out _);
    }

    /// <summary>Filter a dictionary by a glob-like key pattern. Returns all entries when pattern is null/empty/*.</summary>
    private static Dictionary<string, string> FilterByKey(Dictionary<string, string> source, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);

        var regex = GlobToRegex(pattern);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in source)
        {
            if (regex.IsMatch(k))
                result[k] = v;
        }
        return result;
    }

    /// <summary>Convert glob pattern (*, ?) to compiled Regex for in-memory matching (config scope).</summary>
    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".");

        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string FormatEntries(Dictionary<string, string> entries)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in entries)
            sb.AppendLine($"  {k} = {v}");
        return sb.ToString().TrimEnd();
    }

    private sealed record PendingKVOperation(
        bool IsVault,
        string? Key,
        string? Value,
        int? Ttl,
        List<string> Keys)
    {
        public bool Ephemeral { get; init; }
    }

    private sealed class KVInvoker(IKVStore kvStore, IConfigService configService, SubAgentManager subAgentManager, string agentId)
    {
        [Description("Key-value store for reading and writing per-agent data. Supports two scopes: 'vault' (default, persistent key-value table per agent) and 'config' (current agent config with session overrides). Keys support wildcards: * (any sequence) and ? (single char). Empty value = delete key. Use 'set' with a wildcard to do a masked update — it will first dry-run (show matching keys) and require 'accept' to confirm. Calling 'get' or 'set' without a wildcard clears any pending accept.")]
        public async Task<string> Invoke(
            [Description("Action: 'get' to read keys, 'set' to write/delete a key (empty value = delete), 'accept' to confirm a masked set.")] string action,
            [Description("Scope: 'vault' (default, per-agent key-value table) or 'config' (agent config with overrides).")] string scope = "vault",
            [Description("Key name. Supports wildcards: * for any sequence, ? for single char. For 'get': lists matching keys. For 'set' with wildcard: shows dry-run and requires 'accept'. Empty value on 'set' deletes the key.")] string? key = null,
            [Description("Value to set. Empty string ('') DELETES the key entirely.")] string? value = null,
            [Description("TTL in seconds (vault scope only — ignored/config rejected for 'config' scope). After this many seconds the key auto-expires.")] int? ttl = null)
        {
            return await Execute(action, key, value, ttl, scope, kvStore, configService, subAgentManager, agentId);
        }
    }

    public static AIFunction AsKvStoreFunction(IKVStore kvStore, IConfigService configService, SubAgentManager subAgentManager, string agentId)
        => AIFunctionFactory.Create(
            new KVInvoker(kvStore, configService, subAgentManager, agentId).Invoke,
            "kvstore");
}
