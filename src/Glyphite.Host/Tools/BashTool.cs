using System.ComponentModel;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using static Glyphite.Host.Tools.InteractiveConfirmation;

namespace Glyphite.Host.Tools;

public static class BashTool
{
    /// <summary>
    /// Run a shell command behind the safety gates, then execute it.
    /// See <see cref="GateAsync"/> for the gates themselves.
    /// </summary>
    public static async Task<string> ExecuteBash(
        string command,
        string? workdir,
        int? timeoutMs,
        IBashSessionManager manager,
        string agentId,
        ContentDedupOptions dedupOpts,
        BashOptions bashOpts,
        IKVStore? kvStore = null,
        bool isSubAgent = false,
        ISafetyChecker? safetyChecker = null,
        CancellationToken ct = default,
        bool skipInteractiveConfirm = false,
        IConfirmationUi? confirmation = null)
    {
        if (await GateAsync(manager, command, workdir, bashOpts, kvStore, agentId, isSubAgent,
                safetyChecker, skipInteractiveConfirm, confirmation, ct) is { } blocked)
            return blocked;

        try
        {
            timeoutMs ??= 120_000;
            var output = await manager.ExecuteAsync(agentId, command, workdir, timeoutMs, ct);
            return ContentDedup.Compress(output, dedupOpts);
        }
        catch (OperationCanceledException)
        {
            return "Command timed out or was cancelled.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Base directory for a relative <c>workdir</c>: the directory the shell will actually start in.
    /// The manager owns the full precedence (per-agent <c>Bash:DefaultDirectory</c> → agent home →
    /// process default); the per-agent options are checked first because a configured value needs no
    /// lookup. A failed lookup falls back to the process directory — the same last resort the shell
    /// uses — instead of blocking a command for a reason the user cannot see.
    /// </summary>
    private static async Task<string?> StartDirectoryAsync(
        IBashSessionManager manager, string agentId, BashOptions bashOpts, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(bashOpts.DefaultDirectory))
            return bashOpts.DefaultDirectory;

        try
        {
            return await manager.GetStartDirectoryAsync(agentId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The safety gates shared by the foreground and background paths.
    /// Returns null when the command may run, or the message to hand back to the model.
    ///
    /// Three invariants, each of which used to be violated:
    /// <list type="number">
    ///   <item><b>Match the command, not the first word.</b> Patterns are looked up through
    ///         <see cref="CommandAnalyzer"/>, so <c>cd /tmp &amp;&amp; sudo reboot</c>,
    ///         <c>FOO=1 rm -rf ~</c> and <c>bash -c "rm -rf /"</c> are all seen for what they are.</item>
    ///   <item><b>One question per command.</b> However many patterns matched and wherever they sit,
    ///         the human is asked once, about the whole string.</item>
    ///   <item><b>A broken checker is not an approval.</b> When the AI check cannot reach a verdict,
    ///         the decision goes back to the human with the timer off, so silence never means yes.</item>
    /// </list>
    /// </summary>
    private static async Task<string?> GateAsync(
        IBashSessionManager manager,
        string command,
        string? workdir,
        BashOptions bashOpts,
        IKVStore? kvStore,
        string agentId,
        bool isSubAgent,
        ISafetyChecker? safetyChecker,
        bool skipInteractiveConfirm,
        IConfirmationUi? confirmation,
        CancellationToken ct)
    {
        var trimmed = command.Trim();

        // ── 1. Hard block: a forbidden command anywhere in the string ──
        if (CommandAnalyzer.FindMatch(trimmed, bashOpts.ForbiddenCommands) is { } forbidden)
            return $"Command forbidden: '{command}' - matches blocked pattern '{forbidden}'. Try an alternative approach.";

        // ── 2. Forbidden directories ──
        // Compared as paths, not as strings: a relative workdir resolves against the directory the
        // shell will actually start in, so ".." cannot slip past an absolute rule (see PathGuard).
        if (!string.IsNullOrEmpty(workdir))
        {
            var startDir = await StartDirectoryAsync(manager, agentId, bashOpts, ct);
            if (PathGuard.BlockReason(workdir, bashOpts.ForbiddenDirectories, startDir) is { } reason)
                return $"Directory forbidden: '{workdir}' - {reason}. Try an alternative approach.";
        }

        // ── 3. Confirmation patterns: at most ONE prompt, for the whole command ──
        // An approved template (pocket tool, checked when it was created) skips the prompt — but only
        // when nothing was substituted into it; see PocketExecution.CreateRunner.
        if (skipInteractiveConfirm || CommandAnalyzer.FindMatch(trimmed, bashOpts.CheckRequireCommands) is null)
            return null;

        var ui = confirmation ?? ConsoleConfirmationUi.Instance;
        var timeoutMs = bashOpts.CheckRequireCommandsTimeout * 1000;

        var choice = await ui.AskAsync(trimmed, timeoutMs, kvStore, agentId, isSubAgent,
            noAutoSelect: false, note: null, ct);

        switch (choice)
        {
            case Choice.Stop:
                return $"Command blocked by user: '{command}' - requires explicit approval. Model should reconsider or ask the user for clarification.";

            case Choice.Check:
                var verdict = safetyChecker is not null
                    ? await safetyChecker.CheckAsync(agentId, trimmed, ct)
                    : SafetyVerdict.Unavailable("no safety checker configured");

                if (verdict.Inconclusive)
                {
                    // The checker failed, so nobody has judged the command yet. Ask the human — with the
                    // timer off: an automatic choice here would be an automatic "yes".
                    var fallback = await ui.AskAsync(trimmed, timeoutMs, kvStore, agentId, isSubAgent,
                        noAutoSelect: true, note: "safety check unavailable", ct);

                    if (fallback != Choice.Ok)
                        return $"Command blocked: safety check unavailable ({verdict.Why}) and no human approval - '{command}'. "
                             + "Model should ask the user for clarification.";

                    return null;
                }

                return verdict.Allow ? null : $"Command blocked by safety check: '{command}' - {verdict.Why}";

            case Choice.Ok:
            default:
                return null;
        }
    }

    private sealed class BashInvoker(IBashSessionManager manager, string agentId, IConfigService cfg, IKVStore? kvStore, bool isSubAgent, ISafetyChecker? safetyChecker)
    {
        [Description("Execute a bash command in a persistent shell session. Working directory and environment persist between commands. Output is auto-deduplicated (repeated lines compressed). Large outputs are truncated (1/3 top + 2/3 bottom), full output saved to a temp file. Use `workdir` to run in a specific directory (preferred over cd). Use `timeoutMs` for long-running commands. Use `back=true` to run as a background process — returns immediately with a `taskId`. Then use `bash_back` to poll/wait for results. Prefer non-interactive commands: use flags to disable pagers, auto-confirm prompts, provide input via flags rather than stdin.")]
        public async Task<string> Execute(
            [Description("The bash command to execute. Use non-interactive flags where possible (--no-pager, -y, etc.).")] string command,
            string? workdir = null,
            [Description("Timeout in milliseconds (optional, defaults to 120000). Use for long-running builds/tests.")] int? timeoutMs = null,
            [Description("Run in background: returns immediately with a taskId. Use bash_back to get results.")] bool? back = null,
            CancellationToken ct = default)
        {
            var bashOpts = await cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, agentId);

            if (back == true)
            {
                // A detached command is still a command: it passes the same gates before anything spawns.
                if (await GateAsync(manager, command, workdir, bashOpts, kvStore, agentId, isSubAgent,
                        safetyChecker, skipInteractiveConfirm: false, confirmation: null, ct) is { } blocked)
                    return blocked;

                var taskId = await manager.StartBackgroundAsync(agentId, command, workdir, timeoutMs);
                return $"Background task started: {taskId}\nUse `bash_back` with action=\"wait\" or \"partial\" to retrieve output.";
            }

            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, agentId);
            return await ExecuteBash(command, workdir, timeoutMs, manager, agentId, dedupOpts, bashOpts, kvStore, isSubAgent, safetyChecker, ct);
        }
    }

    public static AIFunction AsAIFunction(IBashSessionManager manager, string agentId, IConfigService cfg, IKVStore? kvStore = null, bool isSubAgent = false, ISafetyChecker? safetyChecker = null)
        => AIFunctionFactory.Create(
            new BashInvoker(manager, agentId, cfg, kvStore, isSubAgent, safetyChecker).Execute,
            "bash");
}
