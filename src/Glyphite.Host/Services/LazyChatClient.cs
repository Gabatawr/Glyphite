using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

/// <summary>
/// Wraps an <see cref="IChatClient"/> whose construction is deferred until first use.
/// Allows the app to start even when the LLM API key is not yet configured
/// (e.g. first run before the user edits Glyphite.json).
/// The inner client is created lazily via the provided factory.
/// </summary>
public sealed class LazyChatClient : IChatClient
{
    private readonly Lazy<IChatClient> _lazyInner;

    public LazyChatClient(Func<IChatClient> factory)
    {
        _lazyInner = new Lazy<IChatClient>(factory);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await _lazyInner.Value.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _lazyInner.Value.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return _lazyInner.Value.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        if (_lazyInner.IsValueCreated)
            _lazyInner.Value.Dispose();
    }
}
