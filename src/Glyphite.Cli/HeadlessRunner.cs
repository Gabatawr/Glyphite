using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Glyphite.Cli;

/// <summary>
/// Headless mode: <c>glyphite "task"</c> runs a one-shot temporary subagent (like subagent_run);
/// <c>glyphite "name" "task"</c> runs on a persistent named subagent (like subagent_use, auto-created
/// if missing). Delegates to the exact subagent machinery (scope registration, pending-run crash
/// safety, orphan cleanup, ephemeral vault, block cleanup, background kill) — no duplicated logic.
/// The final answer is written to stdout; errors go to stderr. No interactive prompts.
/// </summary>
public static class HeadlessRunner
{
    public static async Task<int> RunAsync(
        string task, string? agentName, IServiceProvider services, CancellationToken ct)
    {
        var agentStore = services.GetRequiredService<IAgentStore>();
        var agentManager = services.GetRequiredService<IAgentManager>();
        var blockStore = services.GetRequiredService<IBlockStore>();
        var cfgService = services.GetRequiredService<IConfigService>();
        var scopeFactory = services.GetRequiredService<IAgentScopeFactory>();
        var bashManager = services.GetRequiredService<IBashSessionManager>();
        var subAgentManager = services.GetRequiredService<SubAgentManager>();

        var cwd = Directory.GetCurrentDirectory();
        var llm = await cfgService.GetOptionsAsync<LlmOptions>(LlmOptions.Section);

        // Clean up orphan agents from crashed previous runs (same as the subagent tools).
        await SubAgentTool.CleanupOrphanRunsAsync(agentStore, blockStore, subAgentManager);

        string result;
        if (agentName is null)
        {
            // subagent_run without a name: temp GUID agent, created then deleted.
            result = await SubAgentTool.CreateAndRunSubAgentAsync(
                Guid.NewGuid().ToString("N"), task, cwd, cwd, validateName: false,
                subAgentManager, agentManager, scopeFactory, agentStore, blockStore,
                cfgService, bashManager, llm.Model,
                mainSessionId: null, ct, cwd);
        }
        else
        {
            if (!AgentManager.IsValidAgentName(agentName))
            {
                Console.Error.WriteLine($"[!] Invalid agent name '{agentName}'.");
                return 2;
            }

            // subagent_use semantics: auto-create if missing, persistent memory across calls.
            if (!await agentStore.AgentExistsAsync(agentName))
                await agentManager.CreateAgentAsync(agentName, llm.Model, cwd, recordLaunch: false);

            result = await SubAgentTool.RunSubAgentTaskAsync(
                agentName, task, ephemeral: false,
                subAgentManager, scopeFactory, agentStore, blockStore,
                mainSessionId: null, llm.Model, ct, cwd);
        }

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[!] {result}");
            return 1;
        }

        Console.WriteLine(result);
        return 0;
    }
}
