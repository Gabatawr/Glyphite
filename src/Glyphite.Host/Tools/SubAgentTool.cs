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
        string mainSessionId, bool saveMemory = false)
    {
        var resolvedModel = await store.GetAgentModelAsync(agentId) ?? "deepseek-v4-flash";
        var chatOptions = new ChatOptions
        {
            ModelId = resolvedModel,
            Temperature = 0.7f,
            MaxOutputTokens = 8192,
        };
        if (saveMemory)
        {
            chatOptions.AdditionalProperties ??= [];
            chatOptions.AdditionalProperties["saveMemory"] = "true";
        }
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

    // ── Tool functions ──

    public static AIFunction AsSubAgentRunFunction(
        SubAgentManager subAgentManager,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IMemoryStore store,
        ISubAgentConfigLoader configLoader,
        IOptions<DeepSeekOptions> deepseekOpts,
        string currentSessionId)
    {
        var deepseek = deepseekOpts.Value;

        return AIFunctionFactory.Create(async (
            [Description("Initial task/instruction for the subagent.")] string task,
            [Description("Agent name (optional — auto-generated GUID if omitted). If name is provided and the agent already exists, runs a dry-clean task (blocks cleared after). If the agent doesn't exist, creates a temporary one and deletes after.")] string? name = null,
            [Description("Working directory (defaults to main agent's cwd).")] string? cwd = null,
            [Description("Execution mode: 'sequential' (default, wait) or 'parallel' (hint for orchestrator).")] string? mode = null,
            [Description("Auto-clean result after tool loop.")] bool? peek = null) =>
        {
            var parentCwd = await store.GetAgentHomePathAsync(currentSessionId) ?? Directory.GetCurrentDirectory();
            var homePath = cwd ?? parentCwd;

            // ── name provided + agent exists → dry-run with cleanup ──
            if (name is not null && await store.AgentExistsAsync(name))
            {
                var scopeErr = await EnsureScope(subAgentManager, scopeFactory, store, name);
                if (scopeErr is not null) return scopeErr;

                try
                {
                    var result = await subAgentManager.RunAsync(name, async s =>
                    {
                        var (output, blockCk) = await RunAgentTask(s, store, name, task, currentSessionId, saveMemory: false);
                        // Clean up blocks and usage after dry-run
                        await store.ClearUsageAsync(name);
                        await store.DeleteBlocksSinceAsync(name, blockCk);
                        return output;
                    });
                    return result;
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            }

            // ── name provided + agent doesn't exist → create temp, run, delete ──
            if (name is not null)
            {
                if (!AgentManager.IsValidAgentName(name))
                    return $"Error: Invalid agent name '{name}'.";

                var agentId = name;
                await agentManager.CreateAgentAsync(agentId, deepseek.Model, homePath);
                await configLoader.LoadConfigAsync(agentId, homePath, parentCwd);

                var scope = scopeFactory.CreateScope();
                if (!subAgentManager.TryRegister(agentId, scope))
                {
                    scope.Dispose();
                    return $"Error: Failed to register subagent '{agentId}'.";
                }

                try
                {
                    var result = await subAgentManager.RunAsync(agentId, async s =>
                    {
                        var (output, _) = await RunAgentTask(s, store, agentId, task, currentSessionId);
                        return output;
                    });
                    return $"{result}";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
                finally
                {
                    subAgentManager.Remove(agentId);
                    await store.DeleteSessionAsync(agentId);
                }
            }

            // ── no name → auto-GUID, temp, run, delete ──
            var guidId = Guid.NewGuid().ToString("N");
            await agentManager.CreateAgentAsync(guidId, deepseek.Model, homePath);

            var guidScope = scopeFactory.CreateScope();
            if (!subAgentManager.TryRegister(guidId, guidScope))
            {
                guidScope.Dispose();
                return $"Error: Failed to register subagent '{guidId}'.";
            }

            try
            {
                var result = await subAgentManager.RunAsync(guidId, async s =>
                {
                    var (output, _) = await RunAgentTask(s, store, guidId, task, currentSessionId);
                    return output;
                });
                return $"{result}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
            finally
            {
                subAgentManager.Remove(guidId);
                await store.DeleteSessionAsync(guidId);
            }
        },
        name: "subagent_run",
        description: "Run a one-shot task on an agent. Without a name: auto-GUID temp agent created then deleted. With a name and agent exists: dry-run (blocks cleaned after). With a name and no agent: temp agent with config created then deleted. Use mode=\"parallel\" for concurrent grouping."
        );
    }

    public static AIFunction AsSubAgentUseFunction(
        SubAgentManager subAgentManager,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IMemoryStore store,
        ISubAgentConfigLoader configLoader,
        IOptions<DeepSeekOptions> deepseekOpts,
        string currentSessionId)
    {
        var deepseek = deepseekOpts.Value;

        return AIFunctionFactory.Create(async (
            [Description("Name of the subagent to execute the task on. Auto-created if not found.")] string name,
            [Description("Task/instruction for the subagent.")] string task,
            [Description("Working directory (used only when auto-creating the agent).")] string? cwd = null,
            [Description("Execution mode: 'sequential' (default, wait for result) or 'parallel' (queues for batch execution via Task.WhenAll).")] string? mode = null,
            [Description("Auto-clean result after tool loop.")] bool? peek = null) =>
        {
            if (!await store.AgentExistsAsync(name))
            {
                if (!AgentManager.IsValidAgentName(name))
                    return $"Error: Invalid agent name '{name}'.";

                var parentCwd = await store.GetAgentHomePathAsync(currentSessionId) ?? Directory.GetCurrentDirectory();
                var homePath = cwd ?? parentCwd;

                await agentManager.CreateAgentAsync(name, deepseek.Model, homePath);
                await configLoader.LoadConfigAsync(name, homePath, parentCwd);
            }

            var scopeErr = await EnsureScope(subAgentManager, scopeFactory, store, name);
            if (scopeErr is not null) return scopeErr;

            try
            {
                var result = await subAgentManager.RunAsync(name, async s =>
                {
                    var (output, _) = await RunAgentTask(s, store, name, task, currentSessionId, saveMemory: true);
                    return output;
                });
                return result;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        },
        name: "subagent_use",
        description: "Execute a task on a named subagent (auto-creates if not found). Memory and context accumulate across calls — the agent persists. Usage delta recorded in main session."
        );
    }

    public static AIFunction AsSubAgentListFunction(
        SubAgentManager subAgentManager,
        IMemoryStore store,
        string currentSessionId)
    {
        return AIFunctionFactory.Create(async (
            [Description("Auto-clean result after tool loop.")] bool? peek = null) =>
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
