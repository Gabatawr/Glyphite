namespace Glyphite.Abstractions.Interfaces;

public record ConfigDiffResult(
    Dictionary<string, string> Updated,
    Dictionary<string, string> Skipped
);

public interface IConfigService
{
    Action<string>? LogAction { get; set; }
    Task<T> GetOptionsAsync<T>(string sectionName, string? sessionId = null) where T : new();
    Task InitializeAsync(HashSet<string>? replaceSections = null);
    Task<Dictionary<string, string>> GetConfigAsync(string? sessionId = null);
    Task<ConfigDiffResult> UpdateConfigAsync(Dictionary<string, string> changes, string scope = "global", string? sessionId = null, CancellationToken ct = default);
    Task DeleteConfigAsync(string[] keys, string scope = "global", string? sessionId = null);
    Task ReplaceSectionAsync(string sectionName, Dictionary<string, string> newKeys, string scope = "global", string? sessionId = null);
    void SetSessionOverlay(string sessionId, Dictionary<string, string> config);
    void ClearSessionOverlay(string sessionId);
}