using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class SafetyCheckerTests
{
    private const string AgentId = "test-agent";

    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly IBlockStore _blocks = Substitute.For<IBlockStore>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();

    private SafetyChecker CreateChecker() => new(_chat, _blocks, _agents, NullLogger<SafetyChecker>.Instance);

    private void QueueResponse(string text, object? raw = null)
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]);
        if (raw is not null)
            response.RawRepresentation = raw;
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
    }

    private List<ChatMessage> CapturedMessages()
        => ((IEnumerable<ChatMessage>)_chat.ReceivedCalls().Single().GetArguments()[0]!).ToList();

    private Task<SafetyVerdict> RunCheckAsync(string command = "rm -rf /")
        => CreateChecker().CheckAsync(AgentId, command, CancellationToken.None);

    // ── Verdict parsing ──

    [Fact]
    public async Task CleanJson_AllowTrue()
    {
        QueueResponse("""{"allow":true,"why":""}""");

        var verdict = await RunCheckAsync();

        Assert.True(verdict.Allow);
    }

    [Fact]
    public async Task CleanJson_DenyWithReason()
    {
        QueueResponse("""{"allow":false,"why":"rm -rf is destructive"}""");

        var verdict = await RunCheckAsync();

        Assert.False(verdict.Allow);
        Assert.Equal("rm -rf is destructive", verdict.Why);
    }

    [Fact]
    public async Task JsonInsideMarkdownFence_IsExtracted()
    {
        QueueResponse("```json\n{\"allow\":false,\"why\":\"no\"}\n```");

        var verdict = await RunCheckAsync();

        Assert.False(verdict.Allow);
    }

    [Fact]
    public async Task MalformedJson_FallsBackToAllow()
    {
        QueueResponse("this is not json at all");

        var verdict = await RunCheckAsync();

        Assert.True(verdict.Allow);
        Assert.Contains("Could not parse", verdict.Why);
    }

    [Fact]
    public async Task EmptyResponse_FallsBackToAllow()
    {
        QueueResponse("   ");

        var verdict = await RunCheckAsync();

        Assert.True(verdict.Allow);
        Assert.Contains("empty response", verdict.Why);
    }

    [Fact]
    public async Task LlmFailure_FallsBackToAllow()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(new InvalidOperationException("llm down")));

        var verdict = await RunCheckAsync();

        Assert.True(verdict.Allow);
        Assert.Contains("Safety check unavailable", verdict.Why);
    }

    // ── Context selection ──

    [Fact]
    public async Task Context_StartsWithBlocksAfterLastTurnMarker()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([
            Block(BlockType.user_message, 1, "old question"),
            Block(BlockType.turn, 2, ""),
            Block(BlockType.user_message, 3, "new question"),
            Block(BlockType.agent_message, 4, "new answer"),
        ]);
        QueueResponse("""{"allow":true,"why":""}""");

        await RunCheckAsync();

        var user = CapturedMessages().Single(m => m.Role == ChatRole.User);
        Assert.Contains("new question", user.Text);
        Assert.Contains("new answer", user.Text);
        Assert.DoesNotContain("old question", user.Text);
    }

    [Fact]
    public async Task Context_NoTurnMarker_StartsWithLastUserMessage()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([
            Block(BlockType.user_message, 1, "first"),
            Block(BlockType.user_message, 2, "second"),
        ]);
        QueueResponse("""{"allow":true,"why":""}""");

        await RunCheckAsync();

        var user = CapturedMessages().Single(m => m.Role == ChatRole.User);
        Assert.Contains("second", user.Text);
        Assert.DoesNotContain("first", user.Text);
    }

    [Fact]
    public async Task Context_OnlyAgentTask_FallsBackToTask()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([
            Block(BlockType.agent_task, 1, "the task"),
            Block(BlockType.agent_message, 2, "the result"),
        ]);
        QueueResponse("""{"allow":true,"why":""}""");

        await RunCheckAsync();

        var user = CapturedMessages().Single(m => m.Role == ChatRole.User);
        Assert.Contains("the task", user.Text);
    }

    [Fact]
    public async Task Context_EmptyStore_SaysNoContext()
    {
        _blocks.LoadBlocksAsync(AgentId).Returns([]);
        QueueResponse("""{"allow":true,"why":""}""");

        await RunCheckAsync();

        var user = CapturedMessages().Single(m => m.Role == ChatRole.User);
        Assert.Contains("(no conversation context)", user.Text);
    }

    // ── Usage recording ──

    [Fact]
    public async Task UsageRecorded_FromOpenAiFormat()
    {
        QueueResponse("""{"allow":true,"why":""}""", raw: new Dictionary<string, object>
        {
            ["usage"] = new Dictionary<string, object>
            {
                ["prompt_tokens"] = 100L,
                ["completion_tokens"] = 50L,
                ["prompt_tokens_details"] = new Dictionary<string, object> { ["cached_tokens"] = 40L },
            },
        });
        _agents.GetAgentModelAsync(AgentId).Returns("model-x");

        var verdict = await RunCheckAsync();

        Assert.True(verdict.Allow);
        await _agents.Received(1).RecordUsageAsync(AgentId, 40, 60, 50, 0, 0, "model-x");
    }

    [Fact]
    public async Task UsageRecordingFailure_DoesNotBlockVerdict()
    {
        QueueResponse("""{"allow":false,"why":"blocked"}""", raw: new Dictionary<string, object>
        {
            ["usage"] = new Dictionary<string, object> { ["prompt_tokens"] = 10L, ["completion_tokens"] = 5L },
        });
        _agents.RecordUsageAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("db down")));

        var verdict = await RunCheckAsync();

        Assert.False(verdict.Allow);
        Assert.Equal("blocked", verdict.Why);
    }

    private static MemoryBlock Block(BlockType type, double number, string content)
    {
        var block = MemoryBlock.Create(type, content);
        block.Number = number;
        return block;
    }
}
