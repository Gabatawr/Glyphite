namespace Glyphite.Abstractions.Interfaces;

public interface IBashSessionManager : IDisposable
{
    Task<string> ExecuteAsync(string agentId, string command, string? workdir = null, int? timeoutMs = null, CancellationToken ct = default);
    void KillSession(string agentId);
    string[] ActiveSessions { get; }
}
