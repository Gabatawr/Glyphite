using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

public class BashSession : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger;
    private TaskCompletionSource? _pendingTcs;
    private string? _pendingMarker;
    private List<string> _pendingOutput = [];
    private readonly object _gate = new();
    private bool _killed;
    private readonly StringBuilder _lineBuf = new();
    private CancellationTokenSource? _readerCts;

    public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    private BashSession(Process process, StreamWriter stdin, ILogger logger)
    {
        _process = process;
        _stdin = stdin;
        _logger = logger;
        _readerCts = new CancellationTokenSource();
        _ = ReadStdoutAsync(_readerCts.Token);
    }

    private async Task ReadStdoutAsync(CancellationToken ct)
    {
        try
        {
            var buf = new byte[65536];
            var chars = new char[65536];
            var decoder = Encoding.UTF8.GetDecoder();
            var stream = _process.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buf, ct);
                if (bytesRead == 0) break;

                var charCount = decoder.GetChars(buf, 0, bytesRead, chars, 0);

                lock (_gate)
                {
                    for (var i = 0; i < charCount; i++)
                    {
                        var c = chars[i];
                        if (c == '\n')
                        {
                            var line = _lineBuf.ToString();
                            _lineBuf.Clear();
                            DispatchLine(line);
                        }
                        else if (c != '\r')
                        {
                            _lineBuf.Append(c);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown requested — expected
        }
        catch (Exception ex)
        {
            try { _process.Kill(entireProcessTree: true); _killed = true; } catch (Exception killEx) { _logger.LogWarning(killEx, "Error killing process on listener failure"); }
            lock (_gate)
            {
                _pendingTcs?.TrySetException(ex);
                _pendingTcs = null;
                _pendingMarker = null;
            }
        }
    }

    private void DispatchLine(string line)
    {
        if (_pendingMarker is null)
        {
            _pendingOutput.Add(line);
            return;
        }

        if (line == _pendingMarker)
        {
            var tcs = _pendingTcs;
            _pendingTcs = null;
            _pendingMarker = null;
            tcs?.TrySetResult();
        }
        else
        {
            _pendingOutput.Add(line);
        }
    }

    public static BashSession Start(BashOptions opts, ILogger logger, string? defaultDirectory = null)
    {
        if (!TryFindBash(opts, logger))
            throw new InvalidOperationException(
                "bash not found. Install Git Bash or WSL, or ensure bash is in PATH.");

        var psi = new ProcessStartInfo
        {
            FileName = opts.ExecutablePath,
            Arguments = opts.Arguments,
            WorkingDirectory = !string.IsNullOrEmpty(opts.DefaultDirectory)
                ? opts.DefaultDirectory
                : defaultDirectory ?? "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var process = new Process { StartInfo = psi };
        process.Start();
        var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        stdin.WriteLine(opts.InitCommand);
        logger.LogDebug("Bash session started (pid: {Pid})", process.Id);
        return new BashSession(process, stdin, logger);
    }

    private static bool TryFindBash(BashOptions opts, ILogger logger)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = opts.ExecutablePath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var found = proc.WaitForExit(opts.DiscoveryTimeoutMs) && proc.ExitCode == 0;
            if (!found)
                logger.LogWarning("Bash not found at '{Path}'", opts.ExecutablePath);
            return found;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error discovering bash at '{Path}'", opts.ExecutablePath);
            return false;
        }
    }

    public async Task<string> ExecuteAsync(string command, string? workdir = null, int? timeoutMs = null, CancellationToken ct = default)
    {
        using var timeoutCts = timeoutMs is > 0
            ? new CancellationTokenSource(timeoutMs.Value)
            : null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutCts is not null)
            timeoutCts.Token.Register(() => linkedCts.Cancel());

        await _lock.WaitAsync(linkedCts.Token);
        try
        {
            LastUsed = DateTime.UtcNow;

            var marker = $"EXEC_END_{Guid.NewGuid():N}";
            var tcs = new TaskCompletionSource();
            var currentOutput = new List<string>();

            lock (_gate)
            {
                _pendingOutput = currentOutput;
                _pendingMarker = marker;
                _pendingTcs = tcs;
            }

            var effectiveCommand = string.IsNullOrEmpty(workdir)
                ? command
                : $"(cd '{workdir.Replace("'", "'\\''")}' && {command})";

            await _stdin.WriteLineAsync($"{effectiveCommand} 2>&1");
            await _stdin.WriteLineAsync($"echo");
            await _stdin.WriteLineAsync($"echo \"{marker}\"");
            await _stdin.FlushAsync();

            using (linkedCts.Token.Register(() =>
            {
                try { _process.Kill(entireProcessTree: true); _killed = true; } catch (Exception killEx) { _logger.LogWarning(killEx, "Error killing process on timeout"); }
                tcs.TrySetCanceled();
            }))
            {
                await tcs.Task;
            }

            lock (_gate)
            {
                var result = string.Join("\n", currentOutput).TrimEnd();
                _pendingMarker = null;
                _pendingTcs = null;
                return result;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _lock.Dispose();
        _stdin.Dispose();
        if (!_killed)
            _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}

public class BashSessionManager : IBashSessionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<BashSession>> _sessions = new();
    private readonly ConcurrentDictionary<string, BackgroundProcessEntry> _background = new();
    private readonly IConfigService _cfgService;
    private readonly ILogger _logger;
    private readonly BashOptions _opts;
    private readonly string? _defaultDirectory;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private long _taskSeq;

    public BashSessionManager(IConfigService cfgService, BashOptions opts, ILogger<BashSessionManager> logger, string? defaultDirectory = null)
    {
        _cfgService = cfgService;
        _logger = logger;
        _opts = opts;
        _defaultDirectory = defaultDirectory;
    }

    public async Task<string> ExecuteAsync(string agentId, string command, string? workdir = null, int? timeoutMs = null, CancellationToken ct = default)
    {
        CleanupIdleSessions();

        // Load fresh BashOptions for new sessions — IOptions<T> singleton may be stale
        var freshOpts = await _cfgService.GetOptionsAsync<BashOptions>(BashOptions.Section);
        var lazy = _sessions.GetOrAdd(agentId, _ => new Lazy<BashSession>(() => BashSession.Start(freshOpts, _logger, _defaultDirectory)));
        BashSession session;
        try
        {
            session = lazy.Value;
        }
        catch
        {
            _sessions.TryRemove(agentId, out _);
            _logger.LogWarning("Failed to start bash session for agent '{AgentId}'", agentId);
            throw;
        }

        try
        {
            return await session.ExecuteAsync(command, workdir, timeoutMs, ct);
        }
        catch (OperationCanceledException)
        {
            _sessions.TryRemove(agentId, out _);
            session.Dispose();
            _logger.LogInformation("Bash session for agent '{AgentId}' cancelled/timeout", agentId);
            throw;
        }
    }

    public void KillSession(string agentId)
    {
        if (_sessions.TryRemove(agentId, out var lazy) && lazy.IsValueCreated)
        {
            lazy.Value.Dispose();
            _logger.LogDebug("Killed bash session for agent '{AgentId}'", agentId);
        }
    }

    public string[] ActiveSessions => _sessions.Where(kv => kv.Value.IsValueCreated).Select(kv => kv.Key).ToArray();

    public void CleanupIdleSessions()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (key, lazy) in _sessions)
        {
            if (lazy.IsValueCreated && lazy.Value.LastUsed < cutoff)
            {
                if (_sessions.TryRemove(key, out var removed))
                {
                    removed.Value.Dispose();
                    _logger.LogDebug("Cleaned up idle bash session for agent '{AgentId}'", key);
                }
            }
        }
    }

    // ── Background processes ──

    public string StartBackgroundAsync(string agentId, string command, string? workdir = null, int? timeoutMs = null)
    {
        var taskId = $"bg_{Interlocked.Increment(ref _taskSeq)}_{Guid.NewGuid().ToString("N")[..8]}";
        var entry = new BackgroundProcessEntry(command, workdir, timeoutMs ?? _opts.DefaultTimeoutMs, _logger);
        _background[taskId] = entry;
        entry.Start();
        _logger.LogDebug("Background process '{TaskId}' started for agent '{AgentId}': {Command}", taskId, agentId, command);
        return taskId;
    }

    public async Task<(string Output, bool Completed, int? ExitCode)> GetBackgroundOutputAsync(string taskId, bool wait, int? timeoutMs = null, int? partLines = null)
    {
        if (!_background.TryGetValue(taskId, out var entry))
            return ("Background task not found: " + taskId, true, null);

        if (wait)
        {
            var completed = await entry.WaitAsync(timeoutMs ?? _opts.DefaultTimeoutMs);
            if (!completed)
            {
                // Timeout — kill and return what we have
                entry.Kill();
                _background.TryRemove(taskId, out _);
            }
        }

        var output = entry.GetOutput(partLines);
        var status = entry.Status;
        if (status.Exited)
            _background.TryRemove(taskId, out _);

        return (output, status.Exited, status.ExitCode);
    }

    public void KillBackground(string taskId)
    {
        if (_background.TryRemove(taskId, out var entry))
        {
            entry.Kill();
            _logger.LogDebug("Killed background process '{TaskId}'", taskId);
        }
    }

    public BackgroundTaskInfo[] ListBackgroundTasks(string agentId)
    {
        return _background
            .Select(kv =>
            {
                var status = kv.Value.Status;
                return new BackgroundTaskInfo(kv.Key, kv.Value.Command, status.Exited, status.ExitCode);
            })
            .ToArray();
    }

    public void Dispose()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (lazy.IsValueCreated)
                lazy.Value.Dispose();
        }
        _sessions.Clear();

        foreach (var entry in _background.Values)
            entry.Kill();
        _background.Clear();
    }

    // ── Background process entry ──

    private sealed class BackgroundProcessEntry
    {
        public string AgentId { get; }
        public string Command => _command;
        private readonly string _command;
        private readonly string? _workdir;
        private readonly int _timeoutMs;
        private readonly ILogger _logger;
        private Process? _process;
        private readonly StringBuilder _output = new();
        private readonly TaskCompletionSource<bool> _exitTcs = new();
        private int _exited;

        public ProcessStatus Status
        {
            get
            {
                if (_process is null) return new ProcessStatus(true, null);
                try { _process.Refresh(); return new ProcessStatus(_process.HasExited, _process.HasExited ? _process.ExitCode : null); }
                catch { return new ProcessStatus(true, null); }
            }
        }

        public record struct ProcessStatus(bool Exited, int? ExitCode);

        public BackgroundProcessEntry(string command, string? workdir, int timeoutMs, ILogger logger)
        {
            _command = command;
            _workdir = workdir;
            _timeoutMs = timeoutMs;
            _logger = logger;
            AgentId = "shared";
        }

        public void Start()
        {
            var effectiveCommand = string.IsNullOrEmpty(_workdir)
                ? _command
                : $"(cd '{_workdir.Replace("'", "'\\''")}' && {_command})";

            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrEmpty(_workdir))
                psi.WorkingDirectory = _workdir;

#if NET
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(effectiveCommand);
#else
            psi.Arguments = $"-c \"{effectiveCommand.Replace("\"", "\\\"")}\"";
#endif

            _process = new Process { StartInfo = psi };
            _process.Start();

            // Read stdout
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new char[4096];
                    var reader = _process.StandardOutput;
                    int charsRead;
                    while ((charsRead = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        lock (_output)
                        {
                            _output.Append(buf, 0, charsRead);
                        }
                    }
                }
                catch { }
            });

            // Read stderr
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new char[4096];
                    var reader = _process.StandardError;
                    int charsRead;
                    while ((charsRead = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        lock (_output)
                        {
                            _output.Append(buf, 0, charsRead);
                        }
                    }
                }
                catch { }
            });

            // Wait for exit
            _ = Task.Run(async () =>
            {
                try
                {
                    _process.WaitForExit(_timeoutMs);
                }
                catch { }
                Interlocked.Exchange(ref _exited, 1);
                _exitTcs.TrySetResult(true);
            });
        }

        public async Task<bool> WaitAsync(int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await _exitTcs.Task.WaitAsync(cts.Token);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public string GetOutput(int? partLines = null)
        {
            lock (_output)
            {
                var text = _output.ToString();
                if (partLines is > 0)
                {
                    var lines = text.Split('\n');
                    if (lines.Length <= partLines.Value)
                        return text;
                    return string.Join('\n', lines[^partLines.Value..]);
                }
                return text;
            }
        }

        public void Kill()
        {
            if (_process is null) return;
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
            _process.Dispose();
            _process = null;
            _exitTcs.TrySetResult(true);
        }
    }
}