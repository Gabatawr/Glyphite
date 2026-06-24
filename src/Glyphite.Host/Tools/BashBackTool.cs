using System.ComponentModel;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class BashBackTool
{
    private sealed class BashBackInvoker(IBashSessionManager manager, IConfigService cfg, string tmpDir, string agentId)
    {
        [Description("Retrieve output/list tasks for background bash processes started with `back=true`. Action 'list' returns all active + recently completed tasks. Action 'wait' blocks until done (kills on timeout). Action 'partial' returns current output, no kill on timeout. Use `partLines` for last N lines.")]
        public async Task<string> Execute(
            [Description("Task ID (ignored for action='list').")] string taskId,
            [Description("'list' — show all tasks. 'wait' — blocks until done (kills on timeout). 'partial' — returns current output, no kill on timeout.")] string action,
            [Description("Timeout in ms for wait/partial. For 'wait': kills on timeout. For 'partial': returns whatever is available.")] int? timeoutMs = null,
            [Description("Return only the last N lines of output (from bottom).")] int? partLines = null,
            CancellationToken ct = default)
        {
            try
            {
                var act = action?.Trim().ToLowerInvariant();

                // ── List background tasks ──
                if (act == "list")
                {
                    var tasks = manager.ListBackgroundTasks(agentId);
                    if (tasks.Length == 0)
                        return "No background tasks.";

                    var sb = new StringBuilder();
                    sb.AppendLine("Background tasks:");
                    foreach (var t in tasks)
                    {
                        var status = t.Completed
                            ? $"completed (exit: {t.ExitCode?.ToString() ?? "?"})"
                            : "running";
                        sb.AppendLine($"  {t.TaskId}  [{status}]  {t.Command.Trim()}");
                    }
                    return sb.ToString().TrimEnd();
                }

                var wait = act == "wait";

                var (output, completed, exitCode) = await manager.GetBackgroundOutputAsync(taskId, wait, timeoutMs, partLines, ct);

                var raw = output ?? "";

                // Apply dedup compression (same as foreground bash)
                var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, agentId);
                var compressed = ContentDedup.Compress(raw, dedupOpts);

                // Apply truncation (same as bash — 1/3 + 2/3 with full output saved)
                var bashOpts = await cfg.GetOptionsAsync<BashOptions>(BashOptions.Section, agentId);
                var result = BashTool.TruncateOutput(compressed, bashOpts.MaxOutput, tmpDir, agentId);

                if (completed)
                {
                    var code = exitCode is not null ? $"exit code: {exitCode}" : "completed";
                    if (result.Length > 0)
                        result += "\n\n";
                    result += $"[Process {code}]";
                }
                else
                {
                    if (result.Length > 0)
                        result += "\n\n";
                    result += "[Process still running — use bash_back again to get more output]";
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                return "Background task wait was cancelled.";
            }
            catch (Exception ex)
            {
                return $"Error retrieving background output: {ex.Message}";
            }
        }
    }

    public static AIFunction AsAIFunction(IBashSessionManager manager, IConfigService cfg, string tmpDir, string agentId)
        => AIFunctionFactory.Create(
            new BashBackInvoker(manager, cfg, tmpDir, agentId).Execute,
            "bash_back");
}