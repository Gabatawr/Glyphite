using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;

namespace Glyphite.Host.Services;

public class BashSession : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TaskCompletionSource? _pendingTcs;
    private string? _pendingMarker;
    private List<string> _pendingOutput = [];
    private readonly object _gate = new();
    private bool _killed;

    public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    private BashSession(Process process, StreamWriter stdin)
    {
        _process = process;
        _stdin = stdin;
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnError;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void OnError(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (_gate)
        {
            if (_pendingMarker is null)
            {
                _pendingOutput.Add("[stderr] " + e.Data);
                return;
            }
            _pendingOutput.Add("[stderr] " + e.Data);
        }
    }

    public static BashSession Start(BashOptions opts, string? defaultDirectory = null)
    {
        if (!TryFindBash(opts))
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
        return new BashSession(process, stdin);
    }

    private static bool TryFindBash(BashOptions opts)
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
            return proc.WaitForExit(opts.DiscoveryTimeoutMs) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnOutput(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        TaskCompletionSource? tcs = null;
        lock (_gate)
        {
            if (_pendingMarker is null)
            {
                _pendingOutput.Add(e.Data);
                return;
            }

            if (e.Data == _pendingMarker)
            {
                tcs = _pendingTcs;
                _pendingTcs = null;
                _pendingMarker = null;
            }
            else
            {
                _pendingOutput.Add(e.Data);
            }
        }
        tcs?.TrySetResult();
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
                : $"cd \"{workdir.Replace("\"", "\\\"")}\" && {command}";

            await _stdin.WriteLineAsync($"{effectiveCommand} 2>&1");
            await _stdin.WriteLineAsync($"echo \"{marker}\"");
            await _stdin.FlushAsync();

            using (linkedCts.Token.Register(() =>
            {
                try { _process.Kill(entireProcessTree: true); _killed = true; } catch { }
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
        _process.OutputDataReceived -= OnOutput;
        _lock.Dispose();
        _stdin.Dispose();
        if (!_killed)
            _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}

public class BashSessionManager : IBashSessionManager
{
    private readonly ConcurrentDictionary<string, Lazy<BashSession>> _sessions = new();
    private readonly BashOptions _opts;
    private readonly string? _defaultDirectory;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public BashSessionManager(BashOptions opts, string? defaultDirectory = null)
    {
        _opts = opts;
        _defaultDirectory = defaultDirectory;
    }

    public async Task<string> ExecuteAsync(string sessionId, string command, string? workdir = null, int? timeoutMs = null, CancellationToken ct = default)
    {
        CleanupIdleSessions();

        var lazy = _sessions.GetOrAdd(sessionId, _ => new Lazy<BashSession>(() => BashSession.Start(_opts, _defaultDirectory)));
        BashSession session;
        try
        {
            session = lazy.Value;
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            throw;
        }

        try
        {
            return await session.ExecuteAsync(command, workdir, timeoutMs, ct);
        }
        catch (OperationCanceledException)
        {
            _sessions.TryRemove(sessionId, out _);
            session.Dispose();
            throw;
        }
    }

    public void KillSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var lazy) && lazy.IsValueCreated)
            lazy.Value.Dispose();
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
                    removed.Value.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (lazy.IsValueCreated)
                lazy.Value.Dispose();
        }
        _sessions.Clear();
    }
}
