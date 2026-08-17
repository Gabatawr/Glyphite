using System.Diagnostics;
using System.Text.Json;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

public class ToolConfigDecoratorTests
{
    private sealed class CapturingFunction : AIFunction
    {
        public AIFunctionArguments? ReceivedArgs { get; private set; }
        public CancellationToken ReceivedCt { get; private set; }
        public Func<CancellationToken, object?> Body { get; set; } = _ => "ok";

        public override string Name => "capture";
        public override string Description => "capture tool";
        public override JsonElement JsonSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{"arg1":{"type":"string"}}}""").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments? arguments, CancellationToken cancellationToken)
        {
            ReceivedArgs = arguments;
            ReceivedCt = cancellationToken;
            return ValueTask.FromResult(Body(cancellationToken));
        }
    }

    private static AIFunctionArguments Args(params (string K, object? V)[] items)
        => new(new Dictionary<string, object?>(items.ToDictionary(i => i.K, i => i.V)));

    private static Dictionary<string, object?> ExtraCfg(params (string K, object V)[] items)
        => new(items.ToDictionary(i => i.K, i => (object?)i.V));

    // ── Schema injection ──

    [Fact]
    public void JsonSchema_IncludesExtraCfg_AndPreservesOriginalArgs()
    {
        var decorated = new ToolConfigDecorator(new CapturingFunction(), peekDefault: true, contentMaxSizeDefault: 5000, timeoutSecondsDefault: 30);

        using var doc = JsonDocument.Parse(decorated.JsonSchema.GetRawText());
        var props = doc.RootElement.GetProperty("properties");

        Assert.True(props.TryGetProperty("arg1", out _)); // original preserved

        var extra = props.GetProperty("extra_cfg");
        Assert.Equal("object", extra.GetProperty("type").GetString());
        Assert.True(extra.GetProperty("properties").TryGetProperty("peek", out _));
        Assert.True(extra.GetProperty("properties").TryGetProperty("timeout", out _));
        Assert.True(extra.GetProperty("properties").TryGetProperty("maxSize", out _));
    }

    // ── Argument forwarding ──

    [Fact]
    public async Task Invoke_NoExtraCfg_ForwardsArgsAndToken()
    {
        var inner = new CapturingFunction();
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1);

        var result = await decorated.InvokeAsync(Args(("arg1", "x")));

        Assert.Equal("ok", result);
        Assert.True(inner.ReceivedArgs!.ContainsKey("arg1"));
        Assert.False(inner.ReceivedArgs.ContainsKey("extra_cfg"));
        Assert.Equal(CancellationToken.None, inner.ReceivedCt);
    }

    [Fact]
    public async Task Invoke_ExtraCfg_StrippedBeforeForwarding()
    {
        var inner = new CapturingFunction();
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1);

        await decorated.InvokeAsync(Args(
            ("arg1", "x"),
            ("extra_cfg", ExtraCfg(("timeout", 1)))));

        Assert.True(inner.ReceivedArgs!.ContainsKey("arg1"));
        Assert.False(inner.ReceivedArgs.ContainsKey("extra_cfg"));
    }

    // ── Timeout enforcement ──

    [Fact]
    public async Task Invoke_ExtraCfgTimeout_CancelsInnerCall()
    {
        var inner = new CapturingFunction
        {
            Body = ct =>
            {
                try
                {
                    Task.Delay(TimeSpan.FromSeconds(30), ct).GetAwaiter().GetResult();
                    return "not-cancelled";
                }
                catch (OperationCanceledException)
                {
                    return "done";
                }
            },
        };
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1);

        var sw = Stopwatch.StartNew();
        var result = await decorated.InvokeAsync(Args(("extra_cfg", ExtraCfg(("timeout", 1)))));
        sw.Stop();

        Assert.Equal("done", result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Invoke_DefaultTimeout_Unlimited_NoCancellation()
    {
        var inner = new CapturingFunction { Body = ct => ct.IsCancellationRequested ? "cancelled" : "ok" };
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1);

        var result = await decorated.InvokeAsync(Args(("arg1", "x")));

        Assert.Equal("ok", result);
    }

    // ── MaxSize enforcement ──

    [Fact]
    public async Task Invoke_MaxSize_Truncates_AndWritesTempFile()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"glyphite_toolcfg_{Guid.NewGuid():N}");
        try
        {
            var inner = new CapturingFunction { Body = _ => new string('x', 10_000) };
            var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1, tmpDir: tmpDir);

            var result = await decorated.InvokeAsync(Args(("extra_cfg", ExtraCfg(("maxSize", 1000))))) as string;

            Assert.NotNull(result);
            Assert.Contains("[Output truncated: showing 1/3", result);
            Assert.Contains("[Full output saved to:", result);
            Assert.StartsWith(new string('x', 333), result);
            Assert.EndsWith(new string('x', 667), result);

            var files = Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories);
            var file = Assert.Single(files);
            Assert.Equal(10_000, new FileInfo(file).Length);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Invoke_MaxSize_NoTmpDir_SimpleTruncation()
    {
        var inner = new CapturingFunction { Body = _ => new string('y', 500) };
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1);

        var result = await decorated.InvokeAsync(Args(("extra_cfg", ExtraCfg(("maxSize", 100))))) as string;

        Assert.NotNull(result);
        Assert.StartsWith(new string('y', 100), result);
        Assert.Contains("[Content truncated at 100 chars", result);
    }

    [Fact]
    public async Task Invoke_MaxSize_UnderLimit_NoTruncation()
    {
        var inner = new CapturingFunction { Body = _ => "short" };
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1);

        var result = await decorated.InvokeAsync(Args(("extra_cfg", ExtraCfg(("maxSize", 1000)))));

        Assert.Equal("short", result);
    }

    [Fact]
    public async Task Invoke_DefaultMaxSize_Unlimited()
    {
        var inner = new CapturingFunction { Body = _ => new string('z', 10_000) };
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: -1);

        var result = await decorated.InvokeAsync(Args(("arg1", "x")));

        Assert.Equal(10_000, ((string)result!).Length);
    }

    // ── Truncate (shared with the streaming pipeline) ──

    [Fact]
    public void Truncate_UnderLimit_ReturnsAsIs()
    {
        var result = ToolConfigDecorator.Truncate("short text", contentMaxSize: 1000, tmpDir: "", toolName: "write_file", agentId: "a");

        Assert.Equal("short text", result);
    }

    [Fact]
    public void Truncate_OverLimit_NoTmpDir_SimpleTruncation()
    {
        var result = ToolConfigDecorator.Truncate(new string('x', 1000), contentMaxSize: 100, tmpDir: "", toolName: "write_file", agentId: "a");

        Assert.StartsWith(new string('x', 100), result);
        Assert.Contains("[Content truncated at 100 chars. Full result length: 1000]", result);
    }

    [Fact]
    public void Truncate_OverLimit_WithTmpDir_SavesFullOutput_Shows13Plus23()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "GlyphiteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var full = new string('y', 900);
            var result = ToolConfigDecorator.Truncate(full, contentMaxSize: 300, tmpDir: tmpDir, toolName: "write_file", agentId: "test-agent");

            Assert.Contains("[Output truncated: showing 1/3 (100 chars) and 2/3 (200 chars) of 900 total]", result);
            Assert.Contains("[Full output saved to:", result);
            // 1/3 top + notice + 2/3 bottom
            Assert.StartsWith(new string('y', 100), result);
            Assert.EndsWith(new string('y', 200), result);

            // Full output is on disk
            var saved = Directory.GetFiles(Path.Combine(tmpDir, "test-agent"), "write_file_*.out");
            var file = Assert.Single(saved);
            Assert.Equal(full, File.ReadAllText(file));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
