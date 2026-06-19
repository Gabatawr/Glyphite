using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Glyphite.Host.Tools;

public static class SubAgentTool
{
    /// <summary>Shared runner: saves usage + block checkpoints, executes task, records delta cost into main
    /// session's usage, returns subagent's text response and block checkpoint (for caller to clean blocks
    /// that were created during this task).</summary>
    private static async Task<(string Result, double BlockCheckpoint)> RunAgentTask(
        AgentScope scope, IMemoryStore store, string agentId, string task,
        string mainSessionId)
    {
        var resolvedModel = await store.GetAgentModelAsync(agentId) ?? "deepseek-v4-flash";
        var chatOptions = new ChatOptions
        {
            ModelId = resolvedModel,
            Temperature = 0.7f,
            MaxOutputTokens = 8192,
        };
        chatOptions.Tools = scope.ToolRegistry.GetBuiltinTools(agentId).ToList();

        // ── Checkpoint: save block number + usage before task ──
        var blockCk = await store.GetNextNumberAsync(agentId);
        var (ckHit, ckMiss, ckOutput) = await store.GetUsageAsync(agentId);

        var sb = new StringBuilder();
        await foreach (var turnEvent in scope.TurnProcessor.ProcessAsync(
            agentId, task, chatOptions, CancellationToken.None))
        {
            if (turnEvent is TextChunkEvent tc)
                sb.Append(tc.Chunk);
        }

        // ── Delta after task completes ──
        var (newHit, newMiss, newOutput) = await store.GetUsageAsync(agentId);
        var dHit = newHit - ckHit;
        var dMiss = newMiss - ckMiss;
        var dOutput = newOutput - ckOutput;

        // Record the subagent's usage delta into the MAIN chat's session_usage
        // Only 3 deltas (hit/miss/output) + model — no lastRequest, no cache rate
        if (dHit > 0 || dMiss > 0 || dOutput > 0)
            await store.RecordUsageAsync(mainSessionId, dHit, dMiss, dOutput,
                model: resolvedModel);

        return (sb.ToString().Trim(), blockCk);
    }

    /// <summary>Ensure a scope is registered for the given agent, creating one if needed.</summary>
    private static async Task<string?> EnsureScope(
        SubAgentManager subAgentManager, IAgentScopeFactory scopeFactory,
        IMemoryStore store, string agentId)
    {
        if (!subAgentManager.Exists(agentId))
        {
            var scope = scopeFactory.CreateScope();
            if (!subAgentManager.TryRegister(agentId, scope))
            {
                scope.Dispose();
                return $"Error: Failed to register scope for '{agentId}'.";
            }
        }
        return null; // ok
    }

    // ── Config loading for subagents ──

    private static async Task LoadSubAgentConfigAsync(
        string agentId, string agentCwd, string parentCwd,
        IConfigService cfgService, IMemoryStore store)
    {
        var merged = new Dictionary<string, string>();

        await ReadAndFlattenConfigFileAsync(Path.Combine(parentCwd, "Glyphite.json"), merged);
        await ReadAndFlattenConfigFileAsync(Path.Combine(parentCwd, $"Glyphite.{agentId}.json"), merged);

        if (!string.Equals(agentCwd, parentCwd, StringComparison.OrdinalIgnoreCase))
            await ReadAndFlattenConfigFileAsync(Path.Combine(agentCwd, $"Glyphite.{agentId}.json"), merged);

        if (merged.Count == 0) return;

        var homePath = await store.GetAgentHomePathAsync(agentId);
        if (string.Equals(homePath, agentCwd, StringComparison.OrdinalIgnoreCase))
            await cfgService.UpdateConfigAsync(merged, scope: "session", sessionId: agentId);
        else
            cfgService.SetSessionOverlay(agentId, merged);
    }

    private static async Task ReadAndFlattenConfigFileAsync(string filePath, Dictionary<string, string> target)
    {
        if (!File.Exists(filePath)) return;
        var json = await File.ReadAllTextAsync(filePath);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "Glyphite", StringComparison.OrdinalIgnoreCase))
                    FlattenJsonElement("Glyphite", prop.Value, target);
            }
        }
    }

    private static void FlattenJsonElement(string prefix, JsonElement el, Dictionary<string, string> result)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    FlattenJsonElement($"{prefix}:{prop.Name}", prop.Value, result);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    FlattenJsonElement($"{prefix}:{i++}", item, result);
                break;
            case JsonValueKind.String:
                result[prefix] = el.GetString() ?? "";
                break;
            case JsonValueKind.Number:
                result[prefix] = el.GetRawText();
                break;
            case JsonValueKind.True:
                result[prefix] = "True";
                break;
            case JsonValueKind.False:
                result[prefix] = "False";
                break;
        }
    }

    // ── Tool functions ──

    public static AIFunction AsSubAgentRunFunction(
        SubAgentManager subAgentManager,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IMemoryStore store,
        IConfigService cfgService,
        IOptions<DeepSeekOptions> deepseekOpts,
        IOptions<AgentOptions> agentOpts,
        string currentSessionId)
    {
        var deepseek = deepseekOpts.Value;

        return AIFunctionFactory.Create(async (
            [Description("Unique name for the subagent. Must not exist. Alphanumeric, dash, underscore (max 100 chars).")] string name,
            [Description("Initial task/instruction for the subagent.")] string task,
            [Description("Working directory (defaults to main agent's cwd).")] string? cwd = null,
            [Description("Execution mode: 'sequential' (default, wait) or 'parallel' (hint for orchestrator).")] string? mode = null) =>
        {
            if (!AgentManager.IsValidAgentName(name))
                return $"Error: Invalid agent name '{name}'.";

            if (await store.AgentExistsAsync(name))
                return $"Error: Agent '{name}' already exists.";

            var parentCwd = await store.GetAgentHomePathAsync(currentSessionId) ?? Directory.GetCurrentDirectory();
            var homePath = cwd ?? parentCwd;

            await agentManager.CreateAgentAsync(name, deepseek.Model, homePath);
            await LoadSubAgentConfigAsync(name, homePath, parentCwd, cfgService, store);

            var scope = scopeFactory.CreateScope();
            if (!subAgentManager.TryRegister(name, scope))
            {
                scope.Dispose();
                return $"Error: Failed to register subagent '{name}'.";
            }

            try
            {
                var result = await subAgentManager.RunAsync(name, async s =>
                {
                    var (output, _) = await RunAgentTask(s, store, name, task, currentSessionId);
                    // Delete agent entirely after one-shot task
                    subAgentManager.Remove(name);
                    await store.DeleteSessionAsync(name);
                    return output;
                });
                return $"{result}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        },
        name: "subagent_run",
        description: "Create a temporary subagent, run the given task, record usage delta into main session, then delete the subagent. One-shot ephemeral agent execution."
        );
    }

    public static AIFunction AsSubAgentUseFunction(
        SubAgentManager subAgentManager,
        IAgentScopeFactory scopeFactory,
        IMemoryStore store,
        IOptions<DeepSeekOptions> deepseekOpts,
        string currentSessionId)
    {
        return AIFunctionFactory.Create(async (
            [Description("Name of an existing subagent to execute the task on.")] string name,
            [Description("Task/instruction for the subagent.")] string task,
            [Description("Working directory (ignored if agent already exists, present for API consistency).")] string? cwd = null,
            [Description("Execution mode: 'sequential' (default, wait for result) or 'parallel' (queues for batch execution via Task.WhenAll).")] string? mode = null) =>
        {
            if (!await store.AgentExistsAsync(name))
                return $"Error: Agent '{name}' not found. Create it with subagent_run first.";

            var scopeErr = await EnsureScope(subAgentManager, scopeFactory, store, name);
            if (scopeErr is not null) return scopeErr;

            var isParallel = string.Equals(mode, "parallel", StringComparison.OrdinalIgnoreCase);

            if (isParallel)
            {
                subAgentManager.EnqueueParallel(name, task, async s =>
                {
                    var (output, blockCk) = await RunAgentTask(s, store, name, task, currentSessionId);
                    // Clear usage + blocks created during this task (delta already in main)
                    await store.ClearUsageAsync(name);
                    await store.DeleteBlocksSinceAsync(name, blockCk);
                    return output;
                });
                return $"[queued parallel] Agent '{name}' will execute in parallel batch.";
            }

            // Sequential: flush any pending parallel queue first, then execute this one
            var flushResults = await subAgentManager.FlushParallelAsync();
            var parts = new List<string>();
            foreach (var (agentId, result, error) in flushResults)
            {
                if (error is not null)
                    parts.Add($"[{agentId}] Error: {error}");
                else
                    parts.Add($"[{agentId}]\n{result}");
            }

            try
            {
                var result = await subAgentManager.RunAsync(name, async s =>
                {
                    var (output, blockCk) = await RunAgentTask(s, store, name, task, currentSessionId);
                    // Clear usage + blocks created during this task (delta already in main)
                    await store.ClearUsageAsync(name);
                    await store.DeleteBlocksSinceAsync(name, blockCk);
                    return output;
                });

                if (parts.Count > 0)
                    parts.Add($"[{name}]\n{result}");
                else
                    parts.Add(result);

                return string.Join("\n\n---\n\n", parts);
            }
            catch (Exception ex)
            {
                parts.Add($"[{name}] Error: {ex.Message}");
                return string.Join("\n\n---\n\n", parts);
            }
        },
        name: "subagent_use",
        description: "Execute a task on an existing subagent. Usage delta recorded in main session. Blocks and usage created during this task are cleaned — agent stays fresh for next invocation. Sequential mode also flushes any queued parallel tasks first."
        );
    }

    public static AIFunction AsSubAgentListFunction(
        SubAgentManager subAgentManager,
        IMemoryStore store,
        string currentSessionId)
    {
        return AIFunctionFactory.Create(async () =>
        {
            var allAgents = await store.ListAgentsAsync();
            var filtered = allAgents.Where(a => !string.Equals(a, currentSessionId)).ToList();
            if (filtered.Count == 0)
                return "No agents found.";

            var lines = new List<string> { $"Found {filtered.Count} agent(s):", "" };

            foreach (var agentId in filtered.Order())
            {
                var homePath = await store.GetAgentHomePathAsync(agentId) ?? "?";
                var model = await store.GetAgentModelAsync(agentId) ?? "?";
                var blockCount = await store.GetBlockCountAsync(agentId);
                var usage = await store.GetLastUsageAsync(agentId);
                var createdAt = await store.GetAgentCreatedAtAsync(agentId) ?? "?";
                var isSub = subAgentManager.Exists(agentId);

                lines.Add($"  [{agentId}]");
                lines.Add($"    Home:     {homePath}");
                lines.Add($"    Model:    {model}");
                lines.Add($"    Blocks:   {blockCount}");
                lines.Add($"    Created:  {createdAt}");
                lines.Add($"    Cache:    {usage.LastHit:N0} hit / {usage.LastMiss:N0} miss (last turn)");
                lines.Add($"    Context:  ~{usage.Hit + usage.Miss:N0} total tokens");
                if (isSub) lines.Add($"    Status:   active (scope loaded)");
                lines.Add("");
            }

            return string.Join("\n", lines).TrimEnd();
        },
        name: "subagent_list",
        description: "List all available agents (excluding current session) with home directory, model, block count, and last turn cache stats (hit/miss tokens)."
        );
    }
}
