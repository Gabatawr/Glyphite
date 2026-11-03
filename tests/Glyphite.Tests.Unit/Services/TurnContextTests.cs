using System.Text.Json;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

/// <summary>
/// <c>write_file</c> is touched twice: the tool writes the file, and the turn pipeline re-reads it so the
/// model is shown what actually landed on disk. The re-read used the raw argument from the model, so with
/// a relative path it looked in the process directory instead of the agent's — either falling back to the
/// raw tool output or, worse, showing a same-named file from another directory.
/// </summary>
public class TurnContextTests : IDisposable
{
    private const string AgentId = "test-agent";

    private readonly TestDb _db = new();
    private readonly string _baseDir = Path.Combine(
        Path.GetTempPath(), "GlyphiteTurnCtx_" + Guid.NewGuid().ToString("N"));

    public TurnContextTests() => Directory.CreateDirectory(_baseDir);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best-effort */ }
    }

    private TurnContext CreateContext(string? baseDir, IReadOnlyList<AITool>? tools = null) => new(
        _db.Blocks, _db.Sessions, NullLogger.Instance,
        AgentId, "test-model",
        new FailSafeChatClient(Substitute.For<IChatClient>(), 1, NullLogger.Instance),
        ToolExecutionDefaults.ContentMaxSize,
        baseDir,
        tools);

    /// <summary>A real config wrapper for write_file — its defaults are what "default" means here.</summary>
    private static AITool WriteFileWrapper(bool peekDefault, int contentMaxSizeDefault = -1) => new ToolConfigDecorator(
        FileWriteTool.AsAIFunction(null), peekDefault,
        contentMaxSizeDefault, timeoutSecondsDefault: 0);

    private static ChatResponseUpdate Call(string callId, string path) => new()
    {
        Contents =
        [
            new FunctionCallContent(callId, "write_file",
                new Dictionary<string, object?> { ["path"] = path, ["content"] = "x" })
        ]
    };

    private static ChatResponseUpdate CallWithCfg(string callId, string path, string cfgJson) => new()
    {
        Contents =
        [
            new FunctionCallContent(callId, "write_file",
                new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["content"] = "x",
                    ["extra_cfg"] = JsonDocument.Parse(cfgJson).RootElement.Clone()
                })
        ]
    };

    private static ChatResponseUpdate Result(string callId) => new()
    {
        Contents = [new FunctionResultContent(callId, "Written (1 bytes)")]
    };

    private async Task<MemoryBlock> ToolBlockAsync() =>
        Assert.Single(await _db.Blocks.LoadBlocksAsync(AgentId), b => b.Type == BlockType.tool);

    [Fact]
    public async Task WriteFile_RereadUsesTheAgentsDirectory()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        await File.WriteAllTextAsync(Path.Combine(_baseDir, "out.txt"), "CONTENT-FROM-BASE-DIR");
        var ctx = CreateContext(_baseDir);

        await ctx.ProcessUpdate(Call("call-1", "out.txt"));
        var events = await ctx.ProcessUpdate(Result("call-1"));

        var toolResult = Assert.Single(events.OfType<ToolResultTurnEvent>());
        Assert.Equal("CONTENT-FROM-BASE-DIR", toolResult.Result);
    }

    [Fact]
    public async Task WriteFile_MissingFile_FallsBackToTheRawOutput()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        var ctx = CreateContext(_baseDir);

        await ctx.ProcessUpdate(Call("call-2", "never-written.txt"));
        var events = await ctx.ProcessUpdate(Result("call-2"));

        var toolResult = Assert.Single(events.OfType<ToolResultTurnEvent>());
        Assert.Equal("Written (1 bytes)", toolResult.Result);
    }

    [Fact]
    public async Task RelativePath_IsStoredOnTheToolBlockAsTheModelWroteIt()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        await File.WriteAllTextAsync(Path.Combine(_baseDir, "out.txt"), "CONTENT");
        var ctx = CreateContext(_baseDir);

        await ctx.ProcessUpdate(Call("call-3", "out.txt"));
        await ctx.ProcessUpdate(Result("call-3"));

        var block = await ToolBlockAsync();
        Assert.Contains("out.txt", block.Content);
    }

    // ── extra_cfg: execution plumbing, not an argument ──

    /// <summary>
    /// The decorator strips <c>extra_cfg</c> before the tool sees it, so an explicit value that equals the
    /// tool's own default says nothing about the call. Storing it would read as a deliberate choice on later
    /// turns — the block keeps what was asked for, not a restatement of configuration.
    /// </summary>
    [Fact]
    public async Task ExtraCfgThatOnlyRestatesDefaults_IsNotStored()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        await File.WriteAllTextAsync(Path.Combine(_baseDir, "out.txt"), "CONTENT");
        var ctx = CreateContext(_baseDir, [WriteFileWrapper(peekDefault: true)]);

        await ctx.ProcessUpdate(CallWithCfg("call-4", "out.txt", """{"peek":true}"""));
        await ctx.ProcessUpdate(Result("call-4"));

        var block = await ToolBlockAsync();
        using var doc = JsonDocument.Parse(block.Content);
        Assert.False(doc.RootElement.TryGetProperty("extra_cfg", out _));
        Assert.Equal("out.txt", doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ExtraCfgThatOverridesADefault_IsStored()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        await File.WriteAllTextAsync(Path.Combine(_baseDir, "out.txt"), "CONTENT");
        var ctx = CreateContext(_baseDir, [WriteFileWrapper(peekDefault: true)]);

        await ctx.ProcessUpdate(CallWithCfg("call-5", "out.txt", """{"peek":false,"maxSize":-1}"""));
        await ctx.ProcessUpdate(Result("call-5"));

        var block = await ToolBlockAsync();
        using var doc = JsonDocument.Parse(block.Content);
        Assert.True(doc.RootElement.TryGetProperty("extra_cfg", out var cfg));
        Assert.False(cfg.GetProperty("peek").GetBoolean());
        // maxSize -1 equals the default, so only the real override survives.
        Assert.Single(cfg.EnumerateObject());
    }

    [Fact]
    public async Task ExtraCfgIsKeptAsPassed_WhenTheToolIsUnknown()
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        await File.WriteAllTextAsync(Path.Combine(_baseDir, "out.txt"), "CONTENT");
        var ctx = CreateContext(_baseDir);   // no tool list → no defaults to judge against

        await ctx.ProcessUpdate(CallWithCfg("call-6", "out.txt", """{"peek":true}"""));
        await ctx.ProcessUpdate(Result("call-6"));

        var block = await ToolBlockAsync();
        using var doc = JsonDocument.Parse(block.Content);
        Assert.True(doc.RootElement.TryGetProperty("extra_cfg", out var cfg));
        Assert.True(cfg.GetProperty("peek").GetBoolean());
    }
}
