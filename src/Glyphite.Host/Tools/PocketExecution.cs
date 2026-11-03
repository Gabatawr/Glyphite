using System.Reflection;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

/// <summary>
/// Bridges a pocket entry to the bash pipeline: substitution lives in <see cref="PocketSchema"/>, the
/// gates (safety, directories, approval) in <see cref="BashTool"/> — this class only carries the
/// "already approved at add time?" decision into the run.
/// </summary>
public static class PocketExecution
{
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
                    values[kv.Key] = PocketSchema.ArgToString(kv.Value);
            }

            var missing = entry.Args.Where(a => a.Req && !values.ContainsKey(a.Name)).Select(a => a.Name).ToList();
            if (missing.Count > 0)
                return $"Error: missing required arg(s): {string.Join(", ", missing)}";

            var cmd = PocketSchema.Substitute(entry.Cmd, values, entry.Args);
            var bashOpts = await cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, agentId);
            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, agentId);

            // Approval was granted for the template as it was written. A substituted command is a
            // different command — the values come from the model, and a `raw` argument is inserted
            // unquoted — so it goes through the same runtime gates as a hand-written bash call.
            // Hard block-checks (forbidden commands/directories) apply either way.
            var unsubstituted = string.Equals(cmd, entry.Cmd, StringComparison.Ordinal);

            return await BashTool.ExecuteBash(
                cmd, entry.Cwd, timeoutMs: null, bashManager, agentId,
                dedupOpts, bashOpts, kvStore, isSubAgent, safetyChecker, ct,
                skipInteractiveConfirm: entry.Approved && unsubstituted);
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

        public override string Name => PocketEntry.MaterializedName(_entry.Name);
        public override string Description => PocketSchema.BuildDescription(_entry, _isGlobal);
        public override JsonElement JsonSchema => PocketSchema.BuildSchema(_entry);
        public override MethodInfo? UnderlyingMethod => null;
        public override JsonSerializerOptions JsonSerializerOptions => PocketStore.JsonOpts;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => EmptyProps;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments? args, CancellationToken ct)
            => await _runner(_entry, args, ct);
    }
}
