using System.Collections.Concurrent;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Glyphite.Host.Services;

public enum McpServerStatus
{
    Disabled,
    Connecting,
    Connected,
    Failed
}

public record McpServerInfo(string Name, string Type, McpServerStatus Status, string? Error, int ToolCount);

public class McpService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, string?> _errors = new();
    private readonly ConcurrentDictionary<string, int> _toolCounts = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<AITool>> _toolCache = new();
    private readonly ConcurrentDictionary<string, string> _serverConfigHashes = new();
    private readonly IConfigService _cfg;
    private readonly ILogger _logger;
    private string? _configHash;

    public McpService(IConfigService cfg, ILogger<McpService> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(string? sessionId = null, CancellationToken ct = default)
    {
        var servers = await GetServerConfigAsync(sessionId, ct);
        var newHash = ComputeHash(servers);

        if (newHash != _configHash)
        {
            _configHash = newHash;
            _toolCache.Clear();
            await SyncServersAsync(servers, ct);
        }

        if (_toolCache.IsEmpty)
        {
            foreach (var (name, client) in _clients)
            {
                try
                {
                    var timeoutCt = CreateTimeoutToken(servers.GetValueOrDefault(name), ct);
                    var tools = await client.ListToolsAsync(cancellationToken: timeoutCt);
                    var list = tools.OfType<AIFunction>().Select(t => (AITool)new McpPeekToolAdapter(t)).ToList().AsReadOnly();
                    _toolCache[name] = list;
                    _toolCounts[name] = list.Count;
                    _statuses[name] = McpServerStatus.Connected;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _statuses[name] = McpServerStatus.Failed;
                    _errors[name] = $"Timed out listing tools";
                    _logger.LogWarning("Timed out listing tools from '{Name}'", name);
                }
                catch (Exception ex)
                {
                    _statuses[name] = McpServerStatus.Failed;
                    _errors[name] = ex.Message;
                    _logger.LogWarning(ex, "Failed to list tools from '{Name}'", name);
                }
            }
        }

        return _toolCache.Values.SelectMany(t => t).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<McpServerInfo>> GetServersAsync(string? sessionId = null, CancellationToken ct = default)
    {
        var servers = await GetServerConfigAsync(sessionId, ct);
        var newHash = ComputeHash(servers);

        if (newHash != _configHash)
        {
            _configHash = newHash;
            _toolCache.Clear();
            await SyncServersAsync(servers, ct);
        }

        var list = new List<McpServerInfo>();
        foreach (var (name, opts) in servers)
        {
            var status = opts.Enabled
                ? _statuses.GetValueOrDefault(name, McpServerStatus.Disabled)
                : McpServerStatus.Disabled;

            var toolCount = _toolCounts.GetValueOrDefault(name);

            list.Add(new McpServerInfo(name, opts.Type, status, _errors.GetValueOrDefault(name), toolCount));
        }
        return list.AsReadOnly();
    }

    public async Task ReconnectAllAsync(string? sessionId = null, CancellationToken ct = default)
    {
        foreach (var (name, client) in _clients)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing client '{Name}'", name); }
        }
        _clients.Clear();
        _statuses.Clear();
        _errors.Clear();
        _toolCache.Clear();
        _serverConfigHashes.Clear();
        _configHash = null;

        var servers = await GetServerConfigAsync(sessionId, ct);
        _configHash = ComputeHash(servers);
        await SyncServersAsync(servers, ct);
    }

    public async Task<McpServerInfo> ReconnectAsync(string name, string? sessionId = null, CancellationToken ct = default)
    {
        var servers = await GetServerConfigAsync(sessionId, ct);
        _toolCache.TryRemove(name, out _);

        if (!servers.TryGetValue(name, out var opts))
            return new McpServerInfo(name, "unknown", McpServerStatus.Failed, "Server not found", 0);

        if (!opts.Enabled)
        {
            if (_clients.TryRemove(name, out var old))
                await old.DisposeAsync();
            _statuses[name] = McpServerStatus.Disabled;
            return new McpServerInfo(name, opts.Type, McpServerStatus.Disabled, null, 0);
        }

        if (_clients.TryRemove(name, out var oldClient))
            await oldClient.DisposeAsync();

        _statuses[name] = McpServerStatus.Connecting;
        _errors.TryRemove(name, out _);

        try
        {
            var timeoutCt = CreateTimeoutToken(opts, ct);
            var client = await CreateClientAsync(name, opts, timeoutCt);
            _clients[name] = client;
            _statuses[name] = McpServerStatus.Connected;
            _errors.TryRemove(name, out _);

            var tools = await client.ListToolsAsync(cancellationToken: timeoutCt);
            var list = tools.OfType<AIFunction>().Select(t => (AITool)new McpPeekToolAdapter(t)).ToList().AsReadOnly();
            _toolCache[name] = list;
            _toolCounts[name] = list.Count;
            _serverConfigHashes[name] = ComputeServerHash(name, opts);
            return new McpServerInfo(name, opts.Type, McpServerStatus.Connected, null, list.Count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _statuses[name] = McpServerStatus.Failed;
            _errors[name] = "Timed out";
            return new McpServerInfo(name, opts.Type, McpServerStatus.Failed, "Timed out", 0);
        }
        catch (Exception ex)
        {
            _statuses[name] = McpServerStatus.Failed;
            _errors[name] = ex.Message;
            return new McpServerInfo(name, opts.Type, McpServerStatus.Failed, ex.Message, 0);
        }
    }

    private async Task<Dictionary<string, McpServerOptions>> GetServerConfigAsync(string? sessionId, CancellationToken ct)
    {
        var config = await _cfg.GetOptionsAsync<McpServersConfig>("McpServers", sessionId);
        return config.Servers;
    }

    private async Task SyncServersAsync(Dictionary<string, McpServerOptions> servers, CancellationToken ct)
    {
        // Remove stale servers (no longer configured)
        foreach (var (name, _) in _clients)
        {
            if (!servers.ContainsKey(name))
            {
                if (_clients.TryRemove(name, out var old))
                    await old.DisposeAsync();
                _statuses.TryRemove(name, out _);
                _errors.TryRemove(name, out _);
                _toolCounts.TryRemove(name, out _);
                _toolCache.TryRemove(name, out _);
                _serverConfigHashes.TryRemove(name, out _);
            }
        }

        // Connect new, re-enabled, or reconfigured servers
        foreach (var (name, opts) in servers)
        {
            if (!opts.Enabled)
            {
                if (_clients.TryRemove(name, out var old))
                    await old.DisposeAsync();
                _statuses[name] = McpServerStatus.Disabled;
                _toolCache.TryRemove(name, out _);
                _serverConfigHashes.TryRemove(name, out _);
                continue;
            }

            var configHash = ComputeServerHash(name, opts);

            // Already connected with same config — skip
            if (_clients.ContainsKey(name) &&
                _serverConfigHashes.TryGetValue(name, out var existingHash) &&
                existingHash == configHash)
                continue;

            // Options changed or new server — reconnect
            if (_clients.TryRemove(name, out var oldClient))
            {
                await oldClient.DisposeAsync();
                _toolCache.TryRemove(name, out _);
                _logger.LogInformation("Reconnecting '{Name}' (config changed)", name);
            }

            _statuses[name] = McpServerStatus.Connecting;
            try
            {
                var timeoutCt = CreateTimeoutToken(opts, ct);
                var client = await CreateClientAsync(name, opts, timeoutCt);
                _clients[name] = client;
                _serverConfigHashes[name] = configHash;
                _statuses[name] = McpServerStatus.Connected;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _statuses[name] = McpServerStatus.Failed;
                _errors[name] = "Timed out";
                _logger.LogWarning("Timed out connecting '{Name}'", name);
            }
            catch (Exception ex)
            {
                _statuses[name] = McpServerStatus.Failed;
                _errors[name] = ex.Message;
                _logger.LogWarning(ex, "Failed to connect '{Name}'", name);
            }
        }
    }

    private static CancellationToken CreateTimeoutToken(McpServerOptions? opts, CancellationToken ct)
    {
        var timeout = opts?.TimeoutSeconds ?? 30;
        if (timeout <= 0) return ct;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        return cts.Token;
    }

    private static async Task<McpClient> CreateClientAsync(string name, McpServerOptions opts, CancellationToken ct)
    {
        switch (opts.Type.ToLowerInvariant())
        {
            case "stdio":
            {
                var transport = new StdioClientTransport(new()
                {
                    Name = name,
                    Command = opts.Command,
                    Arguments = opts.Args,
                });
                return await McpClient.CreateAsync(transport, cancellationToken: ct);
            }
            case "http":
            case "streamablehttp":
            case "sse":
            {
                var transportOpts = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(opts.Url),
                    Name = name,
                    AdditionalHeaders = opts.Headers,
                    TransportMode = HttpTransportMode.AutoDetect,
                };
                var transport = new HttpClientTransport(transportOpts);
                return await McpClient.CreateAsync(transport, cancellationToken: ct);
            }
            default:
                throw new ArgumentException($"Unsupported MCP transport type: {opts.Type}");
        }
    }

    private static string ComputeHash(Dictionary<string, McpServerOptions> servers)
    {
        // Simple content-based hash of server configs to detect changes
        var sb = new System.Text.StringBuilder();
        foreach (var (name, opts) in servers.OrderBy(kv => kv.Key))
        {
            sb.Append(name);
            sb.Append(opts.Enabled);
            sb.Append(opts.Type);
            sb.Append(opts.Command);
            sb.AppendJoin(",", opts.Args ?? []);
            sb.Append(opts.Url);
            sb.Append('|');
        }
        return sb.ToString();
    }

    private static string ComputeServerHash(string name, McpServerOptions opts)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(name);
        sb.Append(opts.Enabled);
        sb.Append(opts.Type);
        sb.Append(opts.Command);
        sb.AppendJoin(",", opts.Args ?? []);
        sb.Append(opts.Url);
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing client on shutdown"); }
        }
        _clients.Clear();
        _toolCache.Clear();
        _serverConfigHashes.Clear();
    }
}

public static class McpServiceExtensions
{
    public static IServiceCollection AddMcp(this IServiceCollection services)
    {
        services.AddSingleton<McpService>();
        return services;
    }
}
