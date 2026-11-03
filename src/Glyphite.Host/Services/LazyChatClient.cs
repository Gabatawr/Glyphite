using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

/// <summary>
/// Wraps an <see cref="IChatClient"/> whose construction is deferred until first use.
/// Allows the app to start even when the LLM API key is not yet configured
/// (e.g. first run before the user edits Glyphite.json).
/// The inner client is created lazily via the provided factory.
///
/// With a <paramref name="keyProvider"/> the inner client is rebuilt whenever the key changes. The
/// provider SDK client captures endpoint, API key and model at construction, so without this the client
/// built on the first turn kept pointing at the old endpoint for the rest of the process — a config edit
/// looked ignored until restart (TODO block 4).
/// </summary>
public sealed class LazyChatClient : IChatClient
{
    private readonly Func<IChatClient> _factory;
    private readonly Func<string>? _keyProvider;
    private readonly object _gate = new();

    private IChatClient? _inner;
    private string? _key;

    public LazyChatClient(Func<IChatClient> factory, Func<string>? keyProvider = null)
    {
        _factory = factory;
        _keyProvider = keyProvider;
    }

    /// <summary>The client for the settings in force right now: built on first use, replaced when the
    /// key moves. Without a key provider this behaves exactly like a <see cref="Lazy{T}"/>.</summary>
    private IChatClient Inner
    {
        get
        {
            var key = _keyProvider?.Invoke();

            lock (_gate)
            {
                if (_inner is not null && (_keyProvider is null || key == _key))
                    return _inner;

                // Retiring the previous client is safe here: the rebuild is triggered by a request that
                // has not been dispatched yet, so nothing is mid-flight on the client being disposed.
                var retired = _inner;
                _inner = _factory();
                _key = key;
                if (retired is not null)
                {
                    try { retired.Dispose(); } catch { /* best effort — the new client is what matters */ }
                }

                return _inner;
            }
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await Inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in Inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return Inner.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        // Never built → nothing to dispose (and no API key needed yet).
        lock (_gate)
        {
            _inner?.Dispose();
            _inner = null;
        }
    }
}
