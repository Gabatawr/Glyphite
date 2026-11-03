using System.Runtime.CompilerServices;
using System.Text;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

/// <summary>
/// <see cref="AgentChatClient"/> pushes <see cref="ChatOptions.AdditionalProperties"/> into the SDK
/// request through <see cref="ChatOptions.RawRepresentationFactory"/>, because MEAI's OpenAI client
/// never copies them. The factory used to be wrapped on every call, while a <see cref="ChatOptions"/>
/// instance lives for the whole session (one instance, every turn, every iteration) — so each request
/// stacked another layer on top of all the previous ones: unbounded growth and O(N) work per request.
/// </summary>
public class AgentChatClientTests
{
    private const string AgentId = "agent-1";
    private const string DeepSeek = "deepseek-chat";

    /// <summary>An inner client that returns nothing — only the options plumbing is under test.</summary>
    private sealed class SilentClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static AgentChatClient Create(string model = DeepSeek)
        => new(new SilentClient(), AgentId, model);

    private static async Task StreamOnceAsync(AgentChatClient client, ChatOptions options)
    {
        await foreach (var _ in client.GetStreamingResponseAsync([], options)) { }
    }

    [Fact]
    public async Task RepeatedRequests_DoNotStackFactoryLayers()
    {
        var options = new ChatOptions { ModelId = DeepSeek };
        var client = Create();

        await StreamOnceAsync(client, options);
        var wrapped = options.RawRepresentationFactory;
        Assert.NotNull(wrapped);

        for (var i = 0; i < 100; i++)
            await StreamOnceAsync(client, options);

        // Same delegate instance: the wrapper recognised itself instead of wrapping again. Before the
        // fix this grew by one layer per call (measured: 1000 calls -> a 1000-deep chain).
        Assert.Same(wrapped, options.RawRepresentationFactory);
    }

    [Fact]
    public async Task NonDeepSeek_IsNotWrapped()
    {
        // No user_id is injected for other providers, so AdditionalProperties stays empty and there is
        // nothing to push into the request — the factory must stay untouched.
        var options = new ChatOptions { ModelId = "gpt-4o" };

        await StreamOnceAsync(Create("gpt-4o"), options);

        Assert.Null(options.RawRepresentationFactory);
    }

    [Fact]
    public async Task ExistingFactory_IsChainedExactlyOnce()
    {
        var innerCalls = 0;
        var options = new ChatOptions
        {
            ModelId = DeepSeek,
            RawRepresentationFactory = _ => { innerCalls++; return null; },
        };
        var client = Create();

        await StreamOnceAsync(client, options);
        await StreamOnceAsync(client, options);

        options.RawRepresentationFactory!(null!);

        // A foreign factory must survive the wrapping (exactly one call), not be dropped.
        Assert.Equal(1, innerCalls);
    }

    [Fact]
    public async Task AdditionalProperties_ReachTheRawOptions()
    {
        var options = new ChatOptions { ModelId = DeepSeek };

        await StreamOnceAsync(Create(), options);

        var raw = options.RawRepresentationFactory!(null!);
        var sdkOptions = Assert.IsType<OpenAI.Chat.ChatCompletionOptions>(raw);
#pragma warning disable SCME0001 // JsonPatch is experimental
        var path = Encoding.UTF8.GetBytes("$.user_id");
        Assert.Equal(AgentId, sdkOptions.Patch.GetString(path));
#pragma warning restore SCME0001
    }
}
