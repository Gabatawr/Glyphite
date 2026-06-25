using System.ComponentModel;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Glyphite.Host.Tools;

internal static class SubAgentTool
{
    /// <summary>Check if an agent ID looks like a GUID (32 hex chars) — temp agent from subagent_run.</summary>
    private static bool IsGuidAgent(string agentId) =>
        agentId.Length == 32 && agentId.All(c => char.IsAsciiHexDigit(c));

    /// <summary>
    /// Clean up orphan agents from crashed subagent_run calls.
    /// Called at the start of each subagent tool lambda.
    /// </summary>
    private static async Task CleanupOrphanRunsAsync(
        IAgentStore agentStore, IBlockStore blockStore,
        SubAgentManager subAgentManager)
    {
        var pending = await agentStore.GetPendingRunsAsync();
        foreach (var (pendingId, mode, blockCk) in pending)
        {
            // Skip if agent is currently active in memory
            if (subAgentManager.Exists(pendingId))
                continue;

            // Skip if agent no longer exists in DB (already cleaned)
            if (!await agentStore.AgentExistsAsync(pendingId))
            {
                await agentStore.ClearPendingRunAsync(pendingId);
                continue;
            }

            try
            {
                if (mode == "run" && IsGuidAgent(pendingId))
                {
                    // GUID temp agent that wasn't cleaned → delete entirely
                    await agentStore.DeleteSessionAsync(pendingId);
                }
                else if (mode is "run" or "run-dry")
                {
                    // Named agent in dry-run/temp mode: clear usage + blocks since checkpoint
                    await agentStore.ClearUsageAsync(pendingId);
                    if (blockCk.HasValue)
                        await blockStore.DeleteBlocksSinceAsync(pendingId, blockCk.Value);
                }
                // "use" mode: nothing to clean — agent is persistent, usage already per-iteration
            }
            finally
            {
                await agentStore.ClearPendingRunAsync(pendingId);
            }
        }
    }

    /// <summary>Shared runner: saves usage + block checkpoints, executes task, records delta cost into main
    /// session's usage, returns subagent's text response and block checkpoint (for caller to clean blocks
    /// that were created during this task).</summary>
    private static async Task<(string Result, double BlockCheckpoint, long CkHit, long CkMiss, long CkOutput)> RunAgentTask(
        AgentScope scope, IAgentStore agentStore, IBlockStore blockStore, string agentId, string task,
        string mainSessionId, string defaultModel, CancellationToken ct, bool ephemeral,
        string? cwd = null)
    {
        var resolvedModel = await agentStore.GetAgentModelAsync(agentId) ?? defaultModel;
        var chatOptions = new ChatOptions
        {
            ModelId = resolvedModel,
        };
        chatOptions.AdditionalProperties ??= [];
        chatOptions.AdditionalProperties["isSubagent"] = "true";
        chatOptions.AdditionalProperties["ephemeral"] = ephemeral ? "true" : "false";
        chatOptions.Tools = (await scope.ToolRegistry.GetBuiltinToolsAsync(agentId, !ephemeral)).ToList();

        // ── Checkpoint: save block number + usage before task ──
        var blockCk = await agentStore.GetNextNumberAsync(agentId);
        var (ckHit, ckMiss, ckOutput) = await agentStore.GetUsageAsync(agentId);

        var sb = new StringBuilder();
        try
        {
            await foreach (var turnEvent in scope.TurnProcessor.ProcessAsync(
                agentId, task, chatOptions, ct, cwd))
            {
                switch (turnEvent)
                {
                    case TextChunkEvent tc:
                        sb.Append(tc.Chunk);
                        break;
                    case ToolCallTurnEvent:
                        // Text before a tool call is the model's planning (what to do next).
                        // Discard it — we only want the final answer text after all tools complete.
                        sb.Clear();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Record delta usage even on cancellation — subagent did complete its last iteration,
            // usage is in DB via OnIterationRecorded, we just need to transfer delta to main session
            await RecordSubAgentDeltaAsync(agentStore, agentId, mainSessionId, ckHit, ckMiss, ckOutput, resolvedModel);

            // Return whatever text the model streamed before cancellation (discard only
            // the text that preceded a tool call — none was executed if we're here).
            // The caller (RunSubAgentTaskAsync) will still clean up ephemeral blocks
            // because it catches OCE separately and re-cleans.
            return (sb.ToString().Trim(), blockCk, ckHit, ckMiss, ckOutput);
        }

        // ── Delta after task completes ──
        await RecordSubAgentDeltaAsync(agentStore, agentId, mainSessionId, ckHit, ckMiss, ckOutput, resolvedModel);

        return (sb.ToString().Trim(), blockCk, ckHit, ckMiss, ckOutput);
    }

    /// <summary>Compute delta since checkpoint and record into main session's usage.</summary>
    private static async Task RecordSubAgentDeltaAsync(
        IAgentStore agentStore, string agentId, string mainSessionId,
        long ckHit, long ckMiss, long ckOutput, string? model)
    {
        var (newHit, newMiss, newOutput) = await agentStore.GetUsageAsync(agentId);
        var dHit = newHit - ckHit;
        var dMiss = newMiss - ckMiss;
        var dOutput = newOutput - ckOutput;

        if (dHit > 0 || dMiss > 0 || dOutput > 0)
            await agentStore.RecordUsageAsync(mainSessionId, dHit, dMiss, dOutput, model: model);
    }

    /// <summary>Ensure a scope is registered for the given agent, creating one if needed.</summary>
    private static async Task<string?> EnsureScope(
        SubAgentManager subAgentManager, IAgentScopeFactory scopeFactory,
        IAgentStore agentStore, string agentId)
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

    /// <summary>
    /// Core runner: ensure scope is registered, execute task, optionally dry-clean blocks.
    /// Does NOT delete the agent session — caller owns lifecycle.
    /// Passes CancellationToken through so Escape from parent interrupts subagent promptly.
    /// </summary>
    private static async Task<string> RunSubAgentTaskAsync(
        string agentId, string task, bool ephemeral,
        SubAgentManager subAgentManager, IAgentScopeFactory scopeFactory,
        IAgentStore agentStore, IBlockStore blockStore, string currentSessionId,
        string defaultModel, CancellationToken ct, string? cwd = null)
    {
        var scopeErr = await EnsureScope(subAgentManager, scopeFactory, agentStore, agentId);
        if (scopeErr is not null) return scopeErr;

        // Record pending run — crash-safe: if process dies, this persists in DB
        var mode = ephemeral ? "run-dry" : "use";
        double? dryCk = null;
        long ckHit = 0, ckMiss = 0, ckOutput = 0;
        if (ephemeral)
        {
            dryCk = await agentStore.GetNextNumberAsync(agentId);
            (ckHit, ckMiss, ckOutput) = await agentStore.GetUsageAsync(agentId);
            await agentStore.SetPendingRunAsync(agentId, mode, dryCk);
        }
        else
        {
            await agentStore.SetPendingRunAsync(agentId, mode);
        }

        try
        {
            return await subAgentManager.RunAsync(agentId, async s =>
            {
                var (output, blockCk, ckHit, ckMiss, ckOutput) = await RunAgentTask(s, agentStore, blockStore, agentId, task, currentSessionId, defaultModel, ct, ephemeral, cwd);
                if (ephemeral)
                {
                    // Restore usage to checkpoint (before the run) instead of clearing everything
                    await agentStore.ClearUsageAsync(agentId);
                    if (ckHit > 0 || ckMiss > 0 || ckOutput > 0)
                        await agentStore.RecordUsageAsync(agentId, ckHit, ckMiss, ckOutput);
                    await blockStore.DeleteBlocksSinceAsync(agentId, blockCk);
                }
                return output;
            });
        }
        catch (OperationCanceledException)
        {
            // Clean up delta blocks even on cancellation — blocks from partial run persist otherwise
            if (ephemeral && dryCk.HasValue)
            {
                await agentStore.ClearUsageAsync(agentId);
                if (ckHit > 0 || ckMiss > 0 || ckOutput > 0)
                    await agentStore.RecordUsageAsync(agentId, ckHit, ckMiss, ckOutput);
                await blockStore.DeleteBlocksSinceAsync(agentId, dryCk.Value);
            }
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
        finally
        {
            await agentStore.ClearPendingRunAsync(agentId);
        }
    }

    /// <summary>
    /// Create a temporary agent, run a task, then delete the session.
    /// Handles scope registration, execution, and cleanup (remove scope + delete session).
    /// On cancellation: finally still runs, agent is cleaned up.
    /// </summary>
    private static async Task<string> CreateAndRunSubAgentAsync(
        string agentId, string task, string homePath, string parentCwd,
        bool validateName,
        SubAgentManager subAgentManager, IAgentManager agentManager,
        IAgentScopeFactory scopeFactory, IAgentStore agentStore, IBlockStore blockStore,
        IConfigService configService, IBashSessionManager bashManager,
        LlmOptions llm,
        string currentSessionId, CancellationToken ct,
        string? cwd = null)
    {
        if (validateName && !AgentManager.IsValidAgentName(agentId))
            return $"Error: Invalid agent name '{agentId}'.";

        // Clean any config overlay from a previous run with this name
        configService.ClearSessionOverlay(agentId);

        await agentManager.CreateAgentAsync(agentId, llm.Model, homePath, recordLaunch: false);

        // Record pending run — crash-safe: if process dies, this persists in DB
        await agentStore.SetPendingRunAsync(agentId, "run");

        try
        {
            subAgentManager.SetEphemeral(agentId, true);
            return await RunSubAgentTaskAsync(agentId, task, ephemeral: true,
                subAgentManager, scopeFactory, agentStore, blockStore, currentSessionId, llm.Model, ct, cwd);
        }
        finally
        {
            bashManager.KillAgentBackgrounds(agentId);
            await agentStore.ClearPendingRunAsync(agentId);
            KVStoreTool.ClearEphemeralVault(agentId);
            subAgentManager.Remove(agentId);
            await agentStore.DeleteSessionAsync(agentId);
        }
    }

    // ── Tool functions ──

    public static AIFunction AsSubAgentRunFunction(
        SubAgentManager subAgentManager,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IAgentStore agentStore,
        IBlockStore blockStore,
        IConfigService configService,
        IBashSessionManager bashManager,
        IOptions<LlmOptions> llmOpts,
        string currentSessionId)
    {
        var llm = llmOpts.Value;

        return AIFunctionFactory.Create(async (
            [Description("Initial task/instruction for the subagent.")] string task,
            [Description("Agent name (optional — auto-generated GUID if omitted). If name is provided and the agent already exists, runs a dry-clean task (blocks cleared after). If the agent doesn't exist, creates a temporary one and deletes after.")] string? name = null,
            string? cwd = null,
            string? mode = null,
            bool? peek = null,
            CancellationToken ct = default) =>
        {
            var parentCwd = await agentStore.GetAgentHomePathAsync(currentSessionId) ?? Directory.GetCurrentDirectory();
            var homePath = cwd ?? parentCwd;

            // Cleanup orphan agents from crashed sessions
            await CleanupOrphanRunsAsync(agentStore, blockStore, subAgentManager);

            // ── name provided + agent exists → dry-run with cleanup ──
            if (name is not null && await agentStore.AgentExistsAsync(name))
            {
                configService.ClearSessionOverlay(name);
                KVStoreTool.ClearEphemeralVault(name);
                subAgentManager.SetEphemeral(name, true);
                try
                {
                    return await RunSubAgentTaskAsync(name, task, ephemeral: true,
                        subAgentManager, scopeFactory, agentStore, blockStore, currentSessionId, llm.Model, ct, cwd);
                }
                finally
                {
                    bashManager.KillAgentBackgrounds(name);
                }
            }

            // ── name provided + agent doesn't exist → create temp, run, delete ──
            if (name is not null)
            {
                return await CreateAndRunSubAgentAsync(name, task, homePath, parentCwd,
                    validateName: true,
                    subAgentManager, agentManager, scopeFactory, agentStore, blockStore,
                    configService, bashManager,
                    llm, currentSessionId, ct, cwd);
            }

            // ── no name → auto-GUID, temp, run, delete ──
            var guidId = Guid.NewGuid().ToString("N");
            return await CreateAndRunSubAgentAsync(guidId, task, homePath, parentCwd,
                validateName: false,
                subAgentManager, agentManager, scopeFactory, agentStore, blockStore,
                configService, bashManager,
                llm, currentSessionId, ct, cwd);
        },
        name: "subagent_run",
        description: "Run a one-shot task on an agent. Without a name: auto-GUID temp agent created then deleted. With a name and agent exists: dry-run (blocks cleaned after). With a name and no agent: temp agent with config created then deleted. Use mode=\"parallel\" for concurrent grouping."
        );
    }

    public static AIFunction AsSubAgentUseFunction(
        SubAgentManager subAgentManager,
        IAgentManager agentManager,
        IAgentScopeFactory scopeFactory,
        IAgentStore agentStore,
        IBlockStore blockStore,
        IOptions<LlmOptions> llmOpts,
        string currentSessionId)
    {
        var llm = llmOpts.Value;

        return AIFunctionFactory.Create(async (
            [Description("Name of the subagent to execute the task on. Auto-created if not found.")] string name,
            [Description("Task/instruction for the subagent.")] string task,
            string? cwd = null,
            string? mode = null,
            bool? peek = null,
            CancellationToken ct = default) =>
        {
            // Cleanup orphan agents from crashed sessions
            await CleanupOrphanRunsAsync(agentStore, blockStore, subAgentManager);

            if (!await agentStore.AgentExistsAsync(name))
            {
                if (!AgentManager.IsValidAgentName(name))
                    return $"Error: Invalid agent name '{name}'.";

                var parentCwd = await agentStore.GetAgentHomePathAsync(currentSessionId) ?? Directory.GetCurrentDirectory();
                var homePath = cwd ?? parentCwd;

                await agentManager.CreateAgentAsync(name, llm.Model, homePath, recordLaunch: false);
            }

            return await RunSubAgentTaskAsync(name, task, ephemeral: false,
                subAgentManager, scopeFactory, agentStore, blockStore, currentSessionId, llm.Model, ct, cwd);
        },
        name: "subagent_use",
        description: "Execute a task on a named subagent (auto-creates if not found). Memory and context accumulate across calls — the agent persists. Usage delta recorded in main session."
        );
    }

    public static AIFunction AsSubAgentListFunction(
        SubAgentManager subAgentManager,
        IAgentStore agentStore,
        IBlockStore blockStore,
        string currentSessionId)
    {
        return AIFunctionFactory.Create(async (
            bool? peek = null) =>
        {
            var allAgents = await agentStore.ListAgentsAsync();
            var filtered = allAgents.Where(a => !string.Equals(a, currentSessionId)).ToList();
            if (filtered.Count == 0)
                return "No agents found.";

            var lines = new List<string> { $"Found {filtered.Count} agent(s):", "" };

            foreach (var agentId in filtered.Order())
            {
                var homePath = await agentStore.GetAgentHomePathAsync(agentId) ?? "?";
                var model = await agentStore.GetAgentModelAsync(agentId) ?? "?";
                var blockCount = await blockStore.GetBlockCountAsync(agentId);
                var usage = await agentStore.GetLastUsageAsync(agentId);
                var createdAt = await agentStore.GetAgentCreatedAtAsync(agentId) ?? "?";
                var isSub = subAgentManager.Exists(agentId);

                lines.Add($"  [{agentId}]");
                lines.Add($"    Home:     {homePath}");
                lines.Add($"    Model:    {model}");
                lines.Add($"    Blocks:   {blockCount}");
                lines.Add($"    Created:  {createdAt}");
                lines.Add($"    Cache:    {ToolCallHelper.FormatK(usage.LastHit)} hit / {ToolCallHelper.FormatK(usage.LastMiss)} miss (last turn)");
                lines.Add($"    Context:  ~{ToolCallHelper.FormatK(usage.LastHit + usage.LastMiss)} (last turn)");
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
