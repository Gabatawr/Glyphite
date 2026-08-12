using System.Text.Json;
using Glyphite.Host.Services;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class FailSafeChatClientTests
{
    private static ChatResponseUpdate TextUpdate(string text)
        => new(ChatRole.Assistant, [new TextContent(text)]);

    private static ChatResponseUpdate ToolCallUpdate(string callId, string name, params (string K, object? V)[] args)
        => new(ChatRole.Assistant,
            [new FunctionCallContent(callId, name, new Dictionary<string, object?>(args.Select(a => new KeyValuePair<string, object?>(a.K, a.V))))]);

    private static AIFunction BashTool() => AIFunctionFactory.Create(BashImpl, name: "bash");
    private static AIFunction ReadTool() => AIFunctionFactory.Create(ReadImpl, name: "read_file");
    private static AIFunction WriteTool() => AIFunctionFactory.Create(WriteImpl, name: "write_file");
    private static AIFunction BoomTool() => AIFunctionFactory.Create(BoomImpl, name: "bash");

    private static string BashImpl(string command) => "ran: " + command;
    private static string ReadImpl(string path) => "content:" + path;
    private static string WriteImpl(string path, string content) => "wrote";
    private static string BoomImpl(string command) => throw new InvalidOperationException("boom");

    private static async Task<List<ChatResponseUpdate>> Consume(
        FailSafeChatClient client, ChatOptions? options, CancellationToken ct = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, ct))
            updates.Add(u);
        return updates;
    }

    // ── Loop mechanics ──

    [Fact]
    public async Task ToolCallThenFinalText_CompletesTwoIterations()
    {
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("call-1", "bash", ("command", "ls"))],
            [TextUpdate("done")]);
        var client = new FailSafeChatClient(fake, maxIterations: 5, NullLogger.Instance);
        var options = new ChatOptions { Tools = [BashTool()] };

        var updates = await Consume(client, options);

        Assert.Equal(2, fake.StreamCallCount);
        Assert.Contains(updates, u => u.Contents.OfType<FunctionCallContent>().Any(f => f.CallId == "call-1"));
        Assert.Contains(updates, u => u.Contents.OfType<FunctionResultContent>().Any(f => f.Result?.ToString() == "ran: ls"));
        Assert.Contains(updates, u => u.Contents.OfType<TextContent>().Any(t => t.Text == "done"));

        // The 2nd LLM call received the rebuilt assistant message + tool result
        var second = fake.ReceivedMessages.ElementAt(1).ToList();
        Assert.Contains(second, m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any(f => f.CallId == "call-1"));
        Assert.Contains(second, m => m.Role == ChatRole.Tool && m.Contents.OfType<FunctionResultContent>().Any(f => f.Result?.ToString() == "ran: ls"));
    }

    [Fact]
    public async Task NoTools_SingleIteration_YieldsText()
    {
        var fake = new FakeChatClient();
        fake.QueueStream([TextUpdate("hello")]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);

        var updates = await Consume(client, options: null);

        Assert.Equal(1, fake.StreamCallCount);
        var text = string.Concat(updates.SelectMany(u => u.Contents).OfType<TextContent>().Select(t => t.Text));
        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task MaxIterationsExceeded_Throws()
    {
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("c1", "bash", ("command", "a"))],
            [ToolCallUpdate("c2", "bash", ("command", "b"))],
            [ToolCallUpdate("c3", "bash", ("command", "c"))]);
        var client = new FailSafeChatClient(fake, maxIterations: 2, NullLogger.Instance);
        var options = new ChatOptions { Tools = [BashTool()] };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Consume(client, options));

        Assert.Contains("exceeded 2 iterations", ex.Message);
        Assert.Equal(2, fake.StreamCallCount);
    }

    [Fact]
    public async Task MissingCallId_IsFixed_AndMessageRebuilt()
    {
        var fake = new FakeChatClient();
        var noIdFcc = new FunctionCallContent(callId: "", name: "bash", arguments: new Dictionary<string, object?> { ["command"] = "ls" });
        fake.QueueStream(
            [new ChatResponseUpdate(ChatRole.Assistant, [noIdFcc])],
            [TextUpdate("done")]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);
        var options = new ChatOptions { Tools = [BashTool()] };

        await Consume(client, options);

        var second = fake.ReceivedMessages.ElementAt(1).ToList();
        var assistant = second.First(m => m.Role == ChatRole.Assistant);
        var fixedFcc = assistant.Contents.OfType<FunctionCallContent>().Single();
        Assert.False(string.IsNullOrEmpty(fixedFcc.CallId));
        Assert.Equal("bash", fixedFcc.Name);

        var toolMsg = second.First(m => m.Role == ChatRole.Tool);
        Assert.Equal(fixedFcc.CallId, toolMsg.Contents.OfType<FunctionResultContent>().Single().CallId);
    }

    // ── Tool grouping ──

    [Fact]
    public async Task ParallelSafeTools_ExecuteInOneIteration()
    {
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("p1", "read_file", ("path", "a")), ToolCallUpdate("p2", "read_file", ("path", "b"))],
            [TextUpdate("done")]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);
        var options = new ChatOptions { Tools = [ReadTool()] };

        var updates = await Consume(client, options);

        Assert.Equal(2, fake.StreamCallCount);
        var results = updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>().Select(f => f.Result?.ToString()).ToList();
        Assert.Contains("content:a", results);
        Assert.Contains("content:b", results);
    }

    [Fact]
    public async Task ToolError_SkipsSubsequentGroups()
    {
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("e1", "bash", ("command", "boom")), ToolCallUpdate("e2", "bash", ("command", "ok"))],
            [TextUpdate("done")]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);
        var options = new ChatOptions { Tools = [BoomTool()] };

        var updates = await Consume(client, options);

        var frcs = updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, frcs.Count);
        Assert.Contains("Error executing", frcs[0].Result?.ToString());
        Assert.Contains("Skipped", frcs[1].Result?.ToString());
    }

    // ── Cancellation ──

    [Fact]
    public async Task CancelMidStream_InnerStreamRunsToCompletion()
    {
        var fake = new FakeChatClient { DelayPerUpdate = TimeSpan.FromMilliseconds(30) };
        fake.QueueStream([TextUpdate("a"), TextUpdate("b")]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);
        using var cts = new CancellationTokenSource();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, cts.Token))
        {
            updates.Add(u);
            cts.Cancel();
        }

        // Inner stream is not cancelled (FailSafe passes CancellationToken.None) — both updates delivered
        Assert.Equal(2, updates.Count);
        Assert.False(fake.StreamTokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task CancelAfterToolResult_ResultsNotFedBackToLlm()
    {
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("c1", "bash", ("command", "ls"))],
            [TextUpdate("never shown")]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);
        var options = new ChatOptions { Tools = [BashTool()] };
        using var cts = new CancellationTokenSource();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, cts.Token))
        {
            updates.Add(u);
            if (u.Contents.OfType<FunctionResultContent>().Any())
                cts.Cancel();
        }

        // Tool executed and result surfaced, but no 2nd LLM iteration
        Assert.Equal(1, fake.StreamCallCount);
        Assert.Contains(updates, u => u.Contents.OfType<FunctionResultContent>().Any(f => f.Result?.ToString() == "ran: ls"));
    }

    // ── Usage ──

    [Fact]
    public async Task UsageRecordedPerIteration_WithDeltasAndTotals()
    {
        using var doc1 = JsonDocument.Parse("""{"usage":{"prompt_tokens":100,"prompt_tokens_details":{"cached_tokens":60},"completion_tokens":40}}""");
        using var doc2 = JsonDocument.Parse("""{"usage":{"prompt_tokens":200,"prompt_tokens_details":{"cached_tokens":120},"completion_tokens":80}}""");
        var fake = new FakeChatClient();
        fake.QueueStream(
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("c1", "bash", new Dictionary<string, object?> { ["command"] = "ls" })])
            { RawRepresentation = doc1.RootElement }],
            [new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]) { RawRepresentation = doc2.RootElement }]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);
        var options = new ChatOptions { Tools = [BashTool()] };

        var deltas = new List<(long Hit, long Miss, long Output)>();
        client.OnIterationRecorded = (hit, miss, output) =>
        {
            deltas.Add((hit, miss, output));
            return Task.CompletedTask;
        };

        await Consume(client, options);

        Assert.Equal(2, deltas.Count);
        Assert.Equal((60, 40, 40), deltas[0]);
        Assert.Equal((120, 80, 80), deltas[1]);
        Assert.Equal(180, client.TotalCacheHitTokens);
        Assert.Equal(120, client.TotalCacheMissTokens);
        Assert.Equal(120, client.TotalOutputTokens);
        Assert.Equal(120, client.LastHitTokens);
        Assert.Equal(80, client.LastMissTokens);
    }

    // ── Peek ──

    [Fact]
    public async Task PeekToolResult_TruncatedAfterConsumption()
    {
        var fake = new FakeChatClient();
        fake.QueueStream(
            [ToolCallUpdate("w1", "write_file", ("path", "x.txt"), ("content", "secret"))],
            [TextUpdate("done")]);
        var client = new FailSafeChatClient(fake, 5, NullLogger.Instance);
        var options = new ChatOptions { Tools = [WriteTool()] };

        await Consume(client, options);

        // write_file is peek by default (legacy) → after the 2nd iteration consumed it,
        // the live message list was truncated to (peek)
        var live = fake.LastLiveMessages!;
        var toolMsg = live.First(m => m.Role == ChatRole.Tool);
        Assert.Equal("(peek)", toolMsg.Contents.OfType<FunctionResultContent>().Single().Result);
        Assert.Equal("(peek)", toolMsg.Contents.OfType<TextContent>().Single().Text);
    }
}
