using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

/// <summary>Wraps an IChatClient to inject the session/user ID as the DeepSeek `user_id` parameter
/// for cache isolation. Each agent (main + sub-agents) gets a unique user ID so DeepSeek
/// maintains separate caches per agent.</summary>
public sealed class SessionChatClient : DelegatingChatClient
{
    private readonly string _sessionId;

    public SessionChatClient(IChatClient inner, string sessionId) : base(inner)
    {
        _sessionId = sessionId;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options,
        CancellationToken cancellationToken = default)
    {
        options = AddUserId(options);
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = AddUserId(options);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    private ChatOptions AddUserId(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties["user_id"] = _sessionId;
        return options;
    }
}
