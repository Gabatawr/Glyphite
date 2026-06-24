using System.ComponentModel;
using System.Text;
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
        string tmpDir,
        bool? peek = null,
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
            var output = await manager.ExecuteAsync(agentId, command, workdir, timeoutMs, ct);
            var compressed = ContentDedup.Compress(output, dedupOpts);
            return TruncateOutput(compressed, bashOpts.MaxOutput, tmpDir, agentId);
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

    internal static string TruncateOutput(string output, int maxChars, string tmpDir, string agentId)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= maxChars)
            return output;

        // Save full output to tmp file
        var agentTmp = Path.Combine(tmpDir, SanitizeForPath(agentId));
        Directory.CreateDirectory(agentTmp);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outPath = Path.Combine(agentTmp, $"bash_{timestamp}.out");

        File.WriteAllText(outPath, output);

        // Build truncated view: 1/3 from top + truncation notice + 2/3 from bottom
        var topChars = maxChars / 3;
        var bottomChars = maxChars - topChars;

        ReadOnlySpan<char> span = output.AsSpan();
        var top = span[..topChars];
        var bottom = span[^bottomChars..];

        var note = $"[Output truncated: showing 1/3 ({topChars} chars) and 2/3 ({bottomChars} chars) of {output.Length} total]\n" +
                   $"[Full output saved to: {outPath}]\n";

        return string.Concat(top.ToString(), "\n", note, bottom.ToString());
    }

    private static string SanitizeForPath(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    private sealed class BashInvoker(IBashSessionManager manager, string agentId, IConfigService cfg, string tmpDir)
    {
        [Description("Execute a bash command in a persistent shell session. Working directory and environment persist between commands. Output is auto-deduplicated (repeated lines compressed). Large outputs are truncated (showing 1/3 from top + 2/3 from bottom) and the full output is saved to a temp file for later reading. Use `workdir` to run in a specific directory (preferred over cd). Use `timeoutMs` for long-running commands. Use `back=true` to run as a background process — returns immediately with a `taskId`. Then use `bash_back` to poll/wait for results. Prefer non-interactive commands: use flags to disable pagers, auto-confirm prompts, provide input via flags rather than stdin.")]
        public async Task<string> Execute(
            [Description("The bash command to execute. Use non-interactive flags where possible (--no-pager, -y, etc.).")] string command,
            string? workdir = null,
            [Description("Timeout in milliseconds (optional, defaults to 120000). Use for long-running builds/tests.")] int? timeoutMs = null,
            [Description("Run in background: returns immediately with a taskId. Use bash_back to get results.")] bool? back = null,
            bool? peek = null,
            CancellationToken ct = default)
        {
            var bashOpts = await cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, agentId);

            if (back == true)
            {
                var taskId = manager.StartBackgroundAsync(agentId, command, workdir, timeoutMs);
                return $"Background task started: {taskId}\nUse `bash_back` with action=\"wait\" or \"partial\" to retrieve output.";
            }

            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, agentId);
            return await ExecuteBash(command, workdir, timeoutMs, manager, agentId, dedupOpts, bashOpts, tmpDir, peek, ct);
        }
    }

    public static AIFunction AsAIFunction(IBashSessionManager manager, string agentId, IConfigService cfg, string tmpDir)
        => AIFunctionFactory.Create(
            new BashInvoker(manager, agentId, cfg, tmpDir).Execute,
            "bash");
}
