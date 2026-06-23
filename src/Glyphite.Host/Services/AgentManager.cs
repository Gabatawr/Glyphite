using System.Linq;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Utils;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

public class AgentManager : IAgentManager
{
    private readonly IAgentStore _store;
    private readonly IConfigService _cfgService;
    private readonly ILogger _logger;

    public AgentManager(IAgentStore store, IConfigService cfgService, ILogger<AgentManager>? logger = null)
    {
        _store = store;
        _cfgService = cfgService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentManager>.Instance;
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
                scope: "session", agentId: sessionId);
            _logger.LogInformation("Created new session {SessionId} for cwd '{Cwd}'", sessionId, cwd);
        }
        return sessionId;
    }

    public async Task<string> CreateNewAsync(string cwd)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        await _store.EnsureSessionAsync(sessionId);
        await _cfgService.UpdateConfigAsync(
            new() { ["Environment:OS"] = OSHelper.DetectOS(), ["Session:WorkingDirectory"] = cwd },
            scope: "session", agentId: sessionId);
        _logger.LogInformation("Created new session {SessionId} for cwd '{Cwd}'", sessionId, cwd);
        return sessionId;
    }

    public async Task<string> CreateAgentAsync(string agentName, string model, string cwd, bool recordLaunch = true)
    {
        // Agent name is the session id
        var sessionId = agentName;
        if (await _store.AgentExistsAsync(sessionId))
        {
            _logger.LogWarning("Attempted to create existing agent '{AgentName}'", agentName);
            throw new InvalidOperationException($"Agent '{agentName}' already exists.");
        }

        await _store.EnsureSessionAsync(sessionId, homePath: cwd);
        await _store.SetAgentModelAsync(sessionId, model);
        if (recordLaunch)
            await _store.RecordLaunchAsync(sessionId, cwd);
        await _cfgService.UpdateConfigAsync(
            new() { ["Environment:OS"] = OSHelper.DetectOS(), ["Session:WorkingDirectory"] = cwd },
            scope: "session", agentId: sessionId);
        _logger.LogInformation("Created agent '{AgentName}' (model: {Model}, home: {Cwd})", agentName, model, cwd);
        return sessionId;
    }

    public static bool IsValidAgentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Any(char.IsWhiteSpace)) return false;
        if (name.Any(char.IsControl)) return false;
        var invalid = Path.GetInvalidFileNameChars();
        return !name.Any(c => invalid.Contains(c)) && name.Length <= 100;
    }
}
