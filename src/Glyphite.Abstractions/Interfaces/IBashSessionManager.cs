namespace Glyphite.Abstractions.Interfaces;

public record struct BackgroundTaskInfo(string TaskId, string Command, bool Completed, int? ExitCode);

public interface IBashSessionManager : IDisposable
{
    /// <summary>Execute a command in the persistent foreground shell session (blocking).</summary>
    Task<string> ExecuteAsync(string agentId, string command, string? workdir = null, int? timeoutMs = null, CancellationToken ct = default);
    void KillSession(string agentId);
    string[] ActiveSessions { get; }

    /// <summary>Directory a fresh shell for this agent starts in — <c>Bash:DefaultDirectory</c>, else the
    /// agent's home, else the process directory. Also the base for relative <c>workdir</c> arguments,
    /// because that is what the shell itself resolves them against.</summary>
    Task<string> GetStartDirectoryAsync(string agentId, CancellationToken ct = default);

    /// <summary>Start a process in background, returns immediately with a taskId. The process starts
    /// in the agent's own directory (see <see cref="ExecuteAsync"/>).</summary>
    Task<string> StartBackgroundAsync(string agentId, string command, string? workdir = null, int? timeoutMs = null, CancellationToken ct = default);
    /// <summary>Get output from a background process. If wait=true, blocks until completion or timeout (then kills). If wait=false, returns accumulated output (timeout just returns what's available). partLines=N returns only last N lines.</summary>
    Task<(string Output, bool Completed, int? ExitCode)> GetBackgroundOutputAsync(string taskId, bool wait, int? timeoutMs = null, int? partLines = null, CancellationToken ct = default);
    /// <summary>Kill a background process by taskId.</summary>
    void KillBackground(string taskId);
    /// <summary>Kill all background processes for an agent.</summary>
    void KillAgentBackgrounds(string agentId);
    /// <summary>List background tasks for an agent. Includes both running and completed (but not yet consumed) tasks.</summary>
    BackgroundTaskInfo[] ListBackgroundTasks(string agentId);
}