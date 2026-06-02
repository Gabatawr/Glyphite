using System.Linq;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Utils;

namespace Glyphite.Host.Services;

public class AgentManager : IAgentManager
{
    private readonly IMemoryStore _store;
    private readonly IConfigService _cfgService;

    public AgentManager(IMemoryStore store, IConfigService cfgService)
    {
        _store = store;
        _cfgService = cfgService;
    }

    public async Task<string> GetOrCreateAsync(string cwd)
    {
        var sessionId = await _store.GetSessionIdByWorkingDirectoryAsync(cwd);
        if (sessionId is null)
        {
            sessionId = Guid.NewGuid().ToString("N");
            await _store.EnsureSessionAsync(sessionId);
            await _cfgService.UpdateConfigAsync(
                new() { ["Environment:OS"] = OSHelper.DetectOS(), ["Session:WorkingDirectory"] = cwd },
                scope: "session", sessionId: sessionId);
        }
        return sessionId;
    }

    public async Task<string> CreateNewAsync(string cwd)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        await _store.EnsureSessionAsync(sessionId);
        await _cfgService.UpdateConfigAsync(
            new() { ["Environment:OS"] = OSHelper.DetectOS(), ["Session:WorkingDirectory"] = cwd },
            scope: "session", sessionId: sessionId);
        return sessionId;
    }

    public async Task<string> CreateAgentAsync(string agentName, string model, string cwd)
    {
        // Agent name is the session id
        var sessionId = agentName;
        if (await _store.AgentExistsAsync(sessionId))
            throw new InvalidOperationException($"Agent '{agentName}' already exists.");

        await _store.EnsureSessionAsync(sessionId, homePath: cwd);
        await _store.SetAgentModelAsync(sessionId, model);
        await _store.RecordLaunchAsync(sessionId, cwd);
        await _cfgService.UpdateConfigAsync(
            new() { ["Environment:OS"] = OSHelper.DetectOS(), ["Session:WorkingDirectory"] = cwd },
            scope: "session", sessionId: sessionId);
        return sessionId;
    }

    public static bool IsValidAgentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var invalid = Path.GetInvalidFileNameChars();
        return !name.Any(c => invalid.Contains(c)) && name.Length <= 100;
    }
}
