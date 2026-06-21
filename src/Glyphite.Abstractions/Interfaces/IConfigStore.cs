namespace Glyphite.Abstractions.Interfaces;

public interface IConfigStore
{
    Task<string?> GetConfigAsync(string key, string scope = "global", string? agentId = null);
    Task UpsertConfigAsync(string key, string value, string scope = "global", string? agentId = null);
    Task DeleteConfigAsync(string key, string scope = "global", string? agentId = null);
    Task DeleteConfigByScopeAsync(string scope, string? agentId = null);
    Task<Dictionary<string, string>> GetMergedConfigAsync(string? agentId = null);
}
