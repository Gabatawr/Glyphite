using System.ComponentModel;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class BashTool
{
    public static async Task<string> ExecuteBash(
        string command,
        string? workdir,
        int? timeoutMs,
        IBashSessionManager manager,
        string agentId,
        ContentDedupOptions dedupOpts,
        BashOptions bashOpts,
        CancellationToken ct = default)
    {
        var trimmed = command.Trim();
        foreach (var forbidden in bashOpts.ForbiddenCommands)
        {
            if (string.IsNullOrEmpty(forbidden)) continue;
            if (trimmed.Equals(forbidden, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(forbidden + " ", StringComparison.OrdinalIgnoreCase))
            {
                return $"Command forbidden: '{command}' - matches blocked pattern '{forbidden}'. Try an alternative approach.";
            }
        }

        if (!string.IsNullOrEmpty(workdir))
        {
            var normalizedWorkdir = workdir.Replace('\\', '/').TrimEnd('/');
            foreach (var forbiddenDir in bashOpts.ForbiddenDirectories)
            {
                if (string.IsNullOrEmpty(forbiddenDir)) continue;
                var normalizedForbidden = forbiddenDir.Replace('\\', '/').TrimEnd('/');
                if (normalizedWorkdir.Equals(normalizedForbidden, StringComparison.OrdinalIgnoreCase) ||
                    normalizedWorkdir.StartsWith(normalizedForbidden + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Directory forbidden: '{workdir}' - matches blocked path '{forbiddenDir}'. Try an alternative approach.";
                }
            }
        }

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

    private sealed class BashInvoker(IBashSessionManager manager, string agentId, IConfigService cfg)
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
                var taskId = manager.StartBackgroundAsync(agentId, command, workdir, timeoutMs);
                return $"Background task started: {taskId}\nUse `bash_back` with action=\"wait\" or \"partial\" to retrieve output.";
            }

            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, agentId);
            return await ExecuteBash(command, workdir, timeoutMs, manager, agentId, dedupOpts, bashOpts, ct);
        }
    }

    public static AIFunction AsAIFunction(IBashSessionManager manager, string agentId, IConfigService cfg)
        => AIFunctionFactory.Create(
            new BashInvoker(manager, agentId, cfg).Execute,
            "bash");
}
