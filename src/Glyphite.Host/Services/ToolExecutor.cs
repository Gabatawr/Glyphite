using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>Executes AIFunction tools, groups them for parallel execution, and tracks peek state.</summary>
public sealed class ToolExecutor
{
    private readonly ILogger _logger;

    public ToolExecutor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>CallIds of tools that were actually executed (non-skipped, non-peek).</summary>
    public HashSet<string> ExecutedCallIds { get; } = [];

    /// <summary>CallIds of peek=true tool calls — result truncated after LLM consumes them.</summary>
    public HashSet<string> PendingPeekCallIds { get; } = [];

    // ── Parallel-safe tool names (can be grouped for concurrent execution) ──
    private static readonly HashSet<string> _parallelSafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "fetch_web", "search_glob", "search_grep", "subagent_use", "subagent_run"
    };

    /// <summary>
    /// Group consecutive parallel-safe tool calls into batches for concurrent execution.
    /// </summary>
    public static List<List<FunctionCallContent>> BuildToolGroups(IReadOnlyList<FunctionCallContent> fccs)
    {
        var groups = new List<List<FunctionCallContent>>();
        var current = new List<FunctionCallContent>();
        var agentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fcc in fccs)
        {
            var name = fcc.Name ?? "";
            var isSafe = _parallelSafeTools.Contains(name);

            if (!isSafe)
            {
                if (current.Count > 0) { groups.Add(current); current = []; agentNames.Clear(); }
                groups.Add([fcc]);
                continue;
            }

            // subagent_use / subagent_run: check mode and duplicate agent names
            if (name is "subagent_use" or "subagent_run")
            {
                string? mode = null;
                if (fcc.Arguments?.TryGetValue("mode", out var modeObj) == true)
                    mode = modeObj?.ToString();
                var isParallelMode = string.Equals(mode, "parallel", StringComparison.OrdinalIgnoreCase);

                if (!isParallelMode)
                {
                    if (current.Count > 0) { groups.Add(current); current = []; agentNames.Clear(); }
                    groups.Add([fcc]);
                    continue;
                }

                // Parallel mode: check for duplicate explicitly-provided agent names
                if (fcc.Arguments?.TryGetValue("name", out var nameObj) == true)
                {
                    var agentName = nameObj?.ToString();
                    if (!string.IsNullOrEmpty(agentName) && !agentNames.Add(agentName))
                    {
                        groups.Add(current);
                        current = [];
                        agentNames.Clear();
                        agentNames.Add(agentName);
                    }
                }
            }

            current.Add(fcc);
        }

        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    /// <summary>Run a single tool and return (resultText, errorText, exception).</summary>
    public static async Task<(string? Result, string? Error, Exception? Exception)> RunToolAsync(
        AIFunction tool, IDictionary<string, object?>? args, CancellationToken ct)
    {
        try
        {
            var argsObj = args is not null ? new AIFunctionArguments(args) : null;
            var r = await tool.InvokeAsync(argsObj, ct);
            return (r?.ToString() ?? "", null, null);
        }
        catch (Exception ex)
        {
            return (null, $"Error executing '{tool.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>Sequential tool execution (non-streaming path). Returns tool result messages.</summary>
    public async Task<List<ChatMessage>> ExecuteTools(
        IReadOnlyList<FunctionCallContent> fccs, ChatOptions? options, CancellationToken ct)
    {
        var tools = options?.Tools?.OfType<AIFunction>().ToList() ?? [];
        var results = new List<ChatMessage>();
        var hasError = false;

        for (var i = 0; i < fccs.Count; i++)
        {
            var fcc = fccs[i];
            var callId = fcc.CallId ?? Guid.NewGuid().ToString("N");

            if (hasError)
            {
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, $"Skipped — previous tool errored"), new TextContent("Skipped — previous tool errored")]));
                continue;
            }

            var tool = tools.FirstOrDefault(t => t.Name == fcc.Name);
            if (tool is null)
            {
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, $"No tool found: '{fcc.Name}'"), new TextContent($"No tool found: '{fcc.Name}'")]));
                continue;
            }

            try
            {
                var args = fcc.Arguments is not null ? new AIFunctionArguments(fcc.Arguments) : null;
                var result = await tool.InvokeAsync(args, ct);
                var resultText = result?.ToString() ?? "";
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result), new TextContent(resultText)]));
                ExecutedCallIds.Add(callId);
            }
            catch (Exception ex)
            {
                var skipped = fccs.Count - i - 1;
                var msg = $"Error executing '{fcc.Name}': {ex.Message}";
                if (skipped > 0) msg += $"; {skipped} tool call(s) were not executed";
                results.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, msg) { Exception = ex }, new TextContent(msg)]));
                ExecutedCallIds.Add(callId);
                hasError = true;
            }
        }

        return results;
    }

    /// <summary>After LLM consumed peek tool results, truncate them to just '(peek)'.</summary>
    public void CleanupPeekTools(List<ChatMessage> messageList)
    {
        if (PendingPeekCallIds.Count == 0) return;

        foreach (var m in messageList)
        {
            if (m.Role != ChatRole.Tool) continue;
            var shouldTruncate = m.Contents.OfType<FunctionResultContent>().Any(frc =>
                PendingPeekCallIds.Contains(frc.CallId));
            if (!shouldTruncate) continue;
            foreach (var frc in m.Contents.OfType<FunctionResultContent>())
                frc.Result = "(peek)";
            foreach (var tc in m.Contents.OfType<TextContent>())
                tc.Text = "(peek)";
        }
        PendingPeekCallIds.Clear();
    }
}
