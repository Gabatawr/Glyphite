using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Glyphite.Tests.Unit.Support;

/// <summary>
/// Scripted <see cref="IChatClient"/> for testing streaming/tool-call loops.
/// Each queued iteration is the update sequence returned by one
/// <c>GetStreamingResponseAsync</c> call. Records everything it receives so
/// tests can assert what was fed back to the LLM.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _streamScript = new();
    private readonly Queue<ChatResponse> _responseScript = new();

    // Fibo summarizes its zones concurrently, so this fake must tolerate parallel calls:
    // the scripted queues and the recording lists are guarded by a single lock.
    private readonly object _gate = new();

    public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = [];
    public List<ChatOptions?> ReceivedOptions { get; } = [];
    public List<CancellationToken> StreamTokens { get; } = [];
    public int StreamCallCount { get; private set; }
    public int ResponseCallCount { get; private set; }

    /// <summary>The live message list passed to the most recent stream call (in-place mutations visible).</summary>
    public List<ChatMessage>? LastLiveMessages { get; private set; }

    public TimeSpan DelayPerUpdate { get; set; } = TimeSpan.Zero;
    public bool ThrowOnStream { get; set; }
    public bool ThrowOnResponse { get; set; }

    public ChatClientMetadata Metadata { get; } = new("fake-chat-client");

    public void QueueStream(params IReadOnlyList<ChatResponseUpdate>[] iterations)
    {
        lock (_gate)
        {
            foreach (var iteration in iterations)
                _streamScript.Enqueue(iteration);
        }
    }

    public void QueueResponse(ChatResponse response)
    {
        lock (_gate)
            _responseScript.Enqueue(response);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response;
        lock (_gate)
        {
            ReceivedMessages.Add(messages.ToList());
            ReceivedOptions.Add(options);
            ResponseCallCount++;
            if (ThrowOnResponse)
                throw new InvalidOperationException("fake response failure");
            response = _responseScript.Count > 0 ? _responseScript.Dequeue() : new ChatResponse();
        }
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatResponseUpdate>? updates;
        lock (_gate)
        {
            ReceivedMessages.Add(messages.ToList());
            ReceivedOptions.Add(options);
            StreamTokens.Add(cancellationToken);
            LastLiveMessages = messages as List<ChatMessage> ?? messages.ToList();
            StreamCallCount++;

            if (ThrowOnStream)
                throw new InvalidOperationException("fake stream failure");

            updates = _streamScript.Count > 0 ? _streamScript.Dequeue() : null;
        }

        if (updates is null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("")]);
            yield break;
        }

        foreach (var update in updates)
        {
            if (DelayPerUpdate > TimeSpan.Zero)
                await Task.Delay(DelayPerUpdate, cancellationToken);
            yield return update;
        }
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
