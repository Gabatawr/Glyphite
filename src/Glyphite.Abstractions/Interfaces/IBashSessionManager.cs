namespace Glyphite.Abstractions.Interfaces;

public interface IBashSessionManager : IDisposable
{
    Task<string> ExecuteAsync(string sessionId, string command, string? workdir = null, int? timeoutMs = null, CancellationToken ct = default);
    void KillSession(string sessionId);
    string[] ActiveSessions { get; }
}
