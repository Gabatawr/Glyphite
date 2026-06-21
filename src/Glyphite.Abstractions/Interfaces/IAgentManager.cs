namespace Glyphite.Abstractions.Interfaces;

public interface IAgentManager
{
    Task<string> GetOrCreateAsync(string cwd);
    Task<string> CreateNewAsync(string cwd);
    Task<string> CreateAgentAsync(string agentName, string model, string cwd, bool recordLaunch = true);
    public static bool IsValidAgentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var invalid = Path.GetInvalidFileNameChars();
        return !name.Any(c => invalid.Contains(c)) && name.Length <= 100;
    }
}
