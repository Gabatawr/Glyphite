using System.ComponentModel;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class BashBackTool
{
    private sealed class BashBackInvoker(IBashSessionManager manager, IConfigService cfg)
    {
        [Description("Retrieve output from a background bash process started with `back=true`. Action 'wait' blocks until the process finishes (or timeout — then kills it). Action 'partial' returns whatever output is available so far without blocking (timeout just returns what's there). Use `partLines` to get only the last N lines of output.")]
        public async Task<string> Execute(
            [Description("Task ID returned by execute_bash with back=true.")] string taskId,
            [Description("'wait' — blocks until done (kills on timeout). 'partial' — returns current output, no kill on timeout.")] string action,
            [Description("Timeout in ms for wait/partial. For 'wait': kills on timeout. For 'partial': returns whatever is available.")] int? timeoutMs = null,
            [Description("Return only the last N lines of output (from bottom).")] int? partLines = null)
        {
            var wait = action?.Trim().ToLowerInvariant() == "wait";

            var (output, completed, exitCode) = await manager.GetBackgroundOutputAsync(taskId, wait, timeoutMs, partLines);

            var raw = output ?? "";

            // Apply dedup compression (same as foreground execute_bash)
            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section);
            var result = ContentDedup.Compress(raw, dedupOpts);

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
    }

    public static AIFunction AsAIFunction(IBashSessionManager manager, IConfigService cfg)
        => AIFunctionFactory.Create(
            new BashBackInvoker(manager, cfg).Execute,
            "bash_back");
}