namespace Glyphite.Abstractions.Interfaces;

public interface IConfigStore
{
    Task<string?> GetConfigAsync(string key, string scope = "global", string? agentId = null);
    Task UpsertConfigAsync(string key, string value, string scope = "global", string? agentId = null);
    Task DeleteConfigAsync(string key, string scope = "global", string? agentId = null);
    Task DeleteConfigByScopeAsync(string scope, string? agentId = null);
    Task<Dictionary<string, string>> GetMergedConfigAsync(string? agentId = null);

    /// <summary>Only the <c>scope = 'session'</c> rows of one agent — what that agent overrides, with
    /// no global layer mixed in. Callers merge it over fresh global config themselves.</summary>
    Task<Dictionary<string, string>> GetSessionConfigAsync(string agentId);
}
