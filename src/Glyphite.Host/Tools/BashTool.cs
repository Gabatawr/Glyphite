using System.ComponentModel;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class BashTool
{
    [Description("Execute a bash command in a persistent shell session. Working directory and environment persist between commands. Output is auto-deduplicated (repeated lines compressed). Use `workdir` to run in a specific directory (preferred over cd in the command). Use `timeoutMs` for long-running commands. Prefer non-interactive commands: use flags to disable pagers (--no-pager), auto-confirm prompts (-y), provide input via flags rather than stdin. For commands that may fail, consider redirecting stderr to stdout (2>&1) so you can see errors.")]
    public static async Task<string> ExecuteBash(
        [Description("The bash command to execute. Use non-interactive flags where possible (--no-pager, -y, etc.)")] string command,
        [Description("Working directory (optional, defaults to session's current directory). Preferred over `cd` in the command.")] string? workdir,
        [Description("Timeout in milliseconds (optional, defaults to 120000). Use for long-running builds/tests.")] int? timeoutMs,
        IBashSessionManager manager,
        string sessionId,
        ContentDedupOptions dedupOpts,
        BashOptions bashOpts,
        [Description("Auto-clean result after tool loop. Bash output cannot be recovered.")] bool? peek = null,
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
            timeoutMs ??= bashOpts.DefaultTimeoutMs;
            var output = await manager.ExecuteAsync(sessionId, command, workdir, timeoutMs, ct);
            var compressed = ContentDedup.Compress(output, dedupOpts);
            return TruncateOutput(compressed, bashOpts.MaxOutputBytes);
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

    private static string TruncateOutput(string output, int maxBytes)
    {
        if (string.IsNullOrEmpty(output) || Encoding.UTF8.GetByteCount(output) <= maxBytes)
            return output;

        var bytes = Encoding.UTF8.GetBytes(output);
        var truncated = Encoding.UTF8.GetString(bytes[..maxBytes]);
        var note = $"[Output truncated to {maxBytes} bytes, showing first {maxBytes} bytes]\n";
        return note + truncated;
    }

    public static AIFunction AsAIFunction(IBashSessionManager manager, string sessionId, IConfigService? cfg)
        => AIFunctionFactory.Create(
            async (string command, string? workdir = null, int? timeoutMs = null, bool? peek = null, CancellationToken ct = default) =>
            {
                var bashOpts = cfg is not null ? await cfg.GetOptionsAsync<BashOptions>("Bash") : new();
                var dedupOpts = cfg is not null ? await cfg.GetOptionsAsync<ContentDedupOptions>("ContentDedup") : new();
                return await ExecuteBash(command, workdir, timeoutMs, manager, sessionId, dedupOpts, bashOpts, peek, ct);
            },
            "execute_bash");
}
