using System.Collections.Concurrent;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    private readonly IOptionsMonitor<McpServersConfig> _config;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public McpService(IOptionsMonitor<McpServersConfig> config)
    {
        _config = config;
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var allTools = new List<AITool>();
        foreach (var (name, client) in _clients)
        {
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                allTools.AddRange(tools);
                _toolCounts[name] = tools.Count;
                _statuses[name] = McpServerStatus.Connected;
            }
            catch (Exception ex)
            {
                _statuses[name] = McpServerStatus.Failed;
                _errors[name] = ex.Message;
                Console.Error.WriteLine($"[MCP] Failed to list tools from '{name}': {ex.Message}");
            }
        }
        return allTools.AsReadOnly();
    }

    public async Task<IReadOnlyList<McpServerInfo>> GetServersAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await GetServersAsync(_config.CurrentValue.Servers, ct);
    }

    public async Task ReconnectAllAsync(CancellationToken ct = default)
    {
        foreach (var (name, client) in _clients)
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
        _clients.Clear();
        _statuses.Clear();
        _errors.Clear();
        _initialized = false;

        await EnsureInitializedAsync(ct);
    }

    public async Task<McpServerInfo> ReconnectAsync(string name, CancellationToken ct = default)
    {
        var servers = _config.CurrentValue.Servers;
        if (!servers.TryGetValue(name, out var opts))
            return new McpServerInfo(name, "unknown", McpServerStatus.Failed, "Server not found", 0);

        if (!opts.Enabled)
        {
            if (_clients.TryRemove(name, out var old))
                await old.DisposeAsync().ConfigureAwait(false);
            _statuses[name] = McpServerStatus.Disabled;
            return new McpServerInfo(name, opts.Type, McpServerStatus.Disabled, null, 0);
        }

        if (_clients.TryRemove(name, out var oldClient))
            await oldClient.DisposeAsync().ConfigureAwait(false);

        _statuses[name] = McpServerStatus.Connecting;
        _errors.TryRemove(name, out _);

        try
        {
            var client = await CreateClientAsync(name, opts, ct).ConfigureAwait(false);
            _clients[name] = client;
            _statuses[name] = McpServerStatus.Connected;
            _errors.TryRemove(name, out _);

            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            _toolCounts[name] = tools.Count;
            return new McpServerInfo(name, opts.Type, McpServerStatus.Connected, null, tools.Count);
        }
        catch (Exception ex)
        {
            _statuses[name] = McpServerStatus.Failed;
            _errors[name] = ex.Message;
            return new McpServerInfo(name, opts.Type, McpServerStatus.Failed, ex.Message, 0);
        }
    }

    private async Task<IReadOnlyList<McpServerInfo>> GetServersAsync(Dictionary<string, McpServerOptions> servers, CancellationToken ct)
    {
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

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            var servers = _config.CurrentValue.Servers;
            foreach (var (name, opts) in servers)
            {
                if (!opts.Enabled)
                {
                    _statuses[name] = McpServerStatus.Disabled;
                    continue;
                }

                _statuses[name] = McpServerStatus.Connecting;
                try
                {
                    var client = await CreateClientAsync(name, opts, ct).ConfigureAwait(false);
                    _clients[name] = client;
                    _statuses[name] = McpServerStatus.Connected;
                }
                catch (Exception ex)
                {
                    _statuses[name] = McpServerStatus.Failed;
                    _errors[name] = ex.Message;
                    Console.Error.WriteLine($"[MCP] Failed to connect '{name}': {ex.Message}");
                }
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
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
                return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
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
                return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            }
            default:
                throw new ArgumentException($"Unsupported MCP transport type: {opts.Type}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
        _clients.Clear();
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
