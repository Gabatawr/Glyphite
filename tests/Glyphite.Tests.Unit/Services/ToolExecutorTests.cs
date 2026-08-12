using Glyphite.Host.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class ToolExecutorTests
{
    private static FunctionCallContent Fc(string callId, string name, params (string K, object? V)[] args)
        => new(callId, name, new Dictionary<string, object?>(args.Select(a => new KeyValuePair<string, object?>(a.K, a.V))));

    // ── BuildToolGroups ──

    [Fact]
    public void BuildToolGroups_NonSafeTools_EachGetOwnGroup()
    {
        var groups = ToolExecutor.BuildToolGroups(
            [Fc("1", "bash", ("command", "a")), Fc("2", "bash", ("command", "b"))]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("bash", groups[0][0].Name);
        Assert.Equal("bash", groups[1][0].Name);
    }

    [Fact]
    public void BuildToolGroups_ConsecutiveSafeTools_GroupedTogether()
    {
        var groups = ToolExecutor.BuildToolGroups(
            [Fc("1", "read_file", ("path", "a")), Fc("2", "read_file", ("path", "b")), Fc("3", "fetch_web", ("url", "x"))]);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void BuildToolGroups_SafeThenNonSafe_Splits()
    {
        var groups = ToolExecutor.BuildToolGroups(
            [Fc("1", "read_file", ("path", "a")), Fc("2", "bash", ("command", "b")), Fc("3", "read_file", ("path", "c"))]);

        Assert.Equal(3, groups.Count);
        Assert.Single(groups[0]);
        Assert.Single(groups[1]);
        Assert.Single(groups[2]);
    }

    [Fact]
    public void BuildToolGroups_SubagentSequential_OwnGroup()
    {
        var groups = ToolExecutor.BuildToolGroups(
            [Fc("1", "subagent_run", ("task", "a")), Fc("2", "subagent_run", ("task", "b"), ("mode", "parallel"))]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("subagent_run", groups[0][0].Name);
        Assert.Equal("subagent_run", groups[1][0].Name);
    }

    [Fact]
    public void BuildToolGroups_SubagentParallel_DistinctNames_OneGroup()
    {
        var groups = ToolExecutor.BuildToolGroups(
            [Fc("1", "subagent_use", ("name", "a"), ("mode", "parallel")), Fc("2", "subagent_use", ("name", "b"), ("mode", "parallel"))]);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void BuildToolGroups_SubagentParallel_DuplicateNames_Split()
    {
        var groups = ToolExecutor.BuildToolGroups(
            [Fc("1", "subagent_use", ("name", "a"), ("mode", "parallel")), Fc("2", "subagent_use", ("name", "a"), ("mode", "parallel"))]);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups[0]);
        Assert.Single(groups[1]);
    }

    // ── RunToolAsync ──

    [Fact]
    public async Task RunToolAsync_ReturnsResult()
    {
        var tool = AIFunctionFactory.Create(BashImpl, name: "bash");

        var (result, error, ex) = await ToolExecutor.RunToolAsync(
            tool, new Dictionary<string, object?> { ["command"] = "ls" }, CancellationToken.None);

        Assert.Equal("ran: ls", result);
        Assert.Null(error);
        Assert.Null(ex);
    }

    [Fact]
    public async Task RunToolAsync_ReturnsError()
    {
        var tool = AIFunctionFactory.Create(BoomImpl, name: "bash");

        var (result, error, ex) = await ToolExecutor.RunToolAsync(
            tool, new Dictionary<string, object?> { ["command"] = "ls" }, CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("boom", error);
        Assert.IsType<InvalidOperationException>(ex);
    }

    // ── CleanupPeekTools ──

    [Fact]
    public void CleanupPeekTools_TruncatesMatchingMessages_AndClearsPending()
    {
        var exec = new ToolExecutor(NullLogger.Instance);
        exec.PendingPeekCallIds.Add("peek-1");
        var frc = new FunctionResultContent("peek-1", "secret output");
        var msg = new ChatMessage(ChatRole.Tool, [frc, new TextContent("secret output")]);
        var other = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("normal-1", "keep me")]);

        exec.CleanupPeekTools([msg, other]);

        Assert.Equal("(peek)", frc.Result);
        Assert.Equal("(peek)", msg.Contents.OfType<TextContent>().Single().Text);
        Assert.Equal("keep me", other.Contents.OfType<FunctionResultContent>().Single().Result);
        Assert.Empty(exec.PendingPeekCallIds);
    }

    private static string BashImpl(string command) => "ran: " + command;
    private static string BoomImpl(string command) => throw new InvalidOperationException("boom");
}
