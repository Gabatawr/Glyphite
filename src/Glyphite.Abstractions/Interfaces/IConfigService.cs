namespace Glyphite.Abstractions.Interfaces;

public record ConfigDiffResult(
    Dictionary<string, string> Updated,
    Dictionary<string, string> Skipped
);

public interface IConfigService
{
    Action<string>? LogAction { get; set; }
    Task<T> GetOptionsAsync<T>(string sectionName, string? agentId = null) where T : new();
    Task InitializeAsync(HashSet<string>? replaceSections = null);
    Task<Dictionary<string, string>> GetConfigAsync(string? agentId = null);
    Task<ConfigDiffResult> UpdateConfigAsync(Dictionary<string, string> changes, string scope = "global", string? agentId = null, CancellationToken ct = default);
    Task DeleteConfigAsync(string[] keys, string scope = "global", string? agentId = null);
    Task ReplaceSectionAsync(string sectionName, Dictionary<string, string> newKeys, string scope = "global", string? agentId = null);
    void SetSessionOverlay(string sessionId, Dictionary<string, string> config);
    void ClearSessionOverlay(string sessionId);
}