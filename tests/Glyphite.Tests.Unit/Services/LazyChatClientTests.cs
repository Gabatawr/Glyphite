using System.Runtime.CompilerServices;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

/// <summary>
/// The inner client is built on first use, so Glyphite starts even without an API key. Two promises
/// follow from that and are easy to break: the factory must run once (not once per call), and it must
/// not run at all when nothing is ever asked of the client.
/// </summary>
public class LazyChatClientTests
{
    private sealed class CountingClient : IChatClient
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Construction_DoesNotCallTheFactory()
    {
        var created = 0;

        _ = new LazyChatClient(() =>
        {
            created++;
            return new CountingClient();
        });

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Factory_RunsOnce_ForRepeatedCalls()
    {
        var created = 0;
        var inner = new CountingClient();
        var client = new LazyChatClient(() =>
        {
            created++;
            return inner;
        });

        await client.GetResponseAsync([]);
        await client.GetResponseAsync([]);

        Assert.Equal(1, created);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Dispose_WithoutUse_DoesNotCreateTheClient()
    {
        var created = 0;
        var client = new LazyChatClient(() =>
        {
            created++;
            return new CountingClient();
        });

        client.Dispose();

        Assert.Equal(0, created);
    }

    [Fact]
    public void Dispose_AfterUse_DisposesTheInnerClient()
    {
        var inner = new CountingClient();
        var client = new LazyChatClient(() => inner);

        _ = client.GetService(typeof(IChatClient)); // forces creation
        client.Dispose();

        Assert.True(inner.Disposed);
    }

    // ── Settings key: the SDK client captures endpoint/key/model at construction ──

    [Fact]
    public async Task ChangedKey_RebuildsTheInnerClient_AndRetiresTheOldOne()
    {
        var settings = "endpoint-a|model-a|key-a";
        var created = new List<CountingClient>();
        var client = new LazyChatClient(
            () =>
            {
                var fresh = new CountingClient();
                created.Add(fresh);
                return fresh;
            },
            () => settings);

        await client.GetResponseAsync([]);
        Assert.Single(created);

        // User edits Glyphite.json: without a rebuild the client keeps talking to the old endpoint.
        settings = "endpoint-b|model-b|key-b";
        await client.GetResponseAsync([]);

        Assert.Equal(2, created.Count);
        Assert.True(created[0].Disposed, "the replaced client must not keep its sockets open");
        Assert.Equal(1, created[1].Calls);
    }

    [Fact]
    public async Task UnchangedKey_KeepsTheSameInnerClient()
    {
        var createdCount = 0;
        var client = new LazyChatClient(
            () => { createdCount++; return new CountingClient(); },
            () => "same|settings|every|time");

        await client.GetResponseAsync([]);
        await client.GetResponseAsync([]);
        await client.GetResponseAsync([]);

        Assert.Equal(1, createdCount);
    }

    [Fact]
    public void KeyProvider_IsNotConsultedBeforeFirstUse()
    {
        // Reading the key costs an IOptionsMonitor hit; a client nothing is ever asked of must not pay it.
        var consulted = 0;
        _ = new LazyChatClient(() => new CountingClient(), () => { consulted++; return "k"; });

        Assert.Equal(0, consulted);
    }
}