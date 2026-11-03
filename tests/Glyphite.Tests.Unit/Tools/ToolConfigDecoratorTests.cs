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
            Assert.True(result.Length <= 1000, $"view is {result.Length} chars");
            Assert.Contains("[Output truncated: 10000 chars total", result);
            Assert.Contains("[Full output saved to:", result);
            Assert.StartsWith("x", result);
            Assert.EndsWith("x", result);

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
        Assert.True(result.Length <= 100, $"view is {result.Length} chars");
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

    // ── The shape real tools actually return ──

    [Fact]
    public async Task Invoke_MaxSize_TruncatesResultsFromARealToolFactory()
    {
        // AIFunctionFactory yields a JsonElement, not a string. A plain `result is string` test
        // matched no tool at all, so `maxSize` was dead configuration: one 2.6 MB fetch_web page
        // went into the history whole and every later request died on the context limit.
        var inner = AIFunctionFactory.Create(() => new string('q', 50_000), "real_tool");
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: 1000, timeoutSecondsDefault: -1);

        var result = await decorated.InvokeAsync(Args());

        var text = Assert.IsType<string>(result);
        Assert.Contains("[Content truncated at 1000 chars. Full result length: 50000]", text);
        Assert.True(text.Length < 1300, $"got {text.Length} chars");
    }

    [Fact]
    public async Task Invoke_MaxSize_RealToolResultUnderLimit_IsLeftAlone()
    {
        var inner = AIFunctionFactory.Create(() => "small", "real_tool");
        var decorated = new ToolConfigDecorator(inner, peekDefault: false, contentMaxSizeDefault: 1000, timeoutSecondsDefault: -1);

        var result = await decorated.InvokeAsync(Args());

        Assert.Equal("small", result?.ToString());
    }

    [Fact]
    public void ResultText_ReadsStringsAndJsonStringsOnly()
    {
        using var jsonString = JsonDocument.Parse("\"abc\"");
        using var jsonObject = JsonDocument.Parse("""{"a":1}""");

        Assert.Equal("abc", ToolConfigDecorator.ResultText("abc"));
        Assert.Equal("abc", ToolConfigDecorator.ResultText(jsonString.RootElement.Clone()));

        // Structured and non-text results are not text — they must stay untouched.
        Assert.Null(ToolConfigDecorator.ResultText(jsonObject.RootElement.Clone()));
        Assert.Null(ToolConfigDecorator.ResultText(42));
        Assert.Null(ToolConfigDecorator.ResultText(null));
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

        Assert.True(result.Length <= 100, $"view is {result.Length} chars");
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

            Assert.True(result.Length <= 300, $"view is {result.Length} chars");
            Assert.Contains("[Output truncated: 900 chars total", result);
            Assert.Contains("[Full output saved to:", result);

            // 1/3 from the top, 2/3 from the bottom, the notice in between.
            var head = result[..result.IndexOf("[Output truncated", StringComparison.Ordinal)].TrimEnd('\n');
            var tail = result[(result.LastIndexOf(']') + 1)..].TrimStart('\n');
            Assert.NotEmpty(head);
            Assert.NotEmpty(tail);
            Assert.All(head, ch => Assert.Equal('y', ch));
            Assert.All(tail, ch => Assert.Equal('y', ch));
            Assert.True(head.Length < tail.Length, "the tail keeps the larger share");

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

    /// <summary>
    /// A limit smaller than the notice is still a limit: with a 5-char budget the view cannot be
    /// ~120 chars of notice plus content. This is the shape that used to be possible, because the
    /// notice was appended after spending the whole budget on content.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(120)]
    public void Truncate_SmallLimit_NeverExceedsIt(int maxSize)
    {
        var view = ToolConfigDecorator.Truncate(
            new string('x', 1000), maxSize, tmpDir: "", toolName: "write_file", agentId: "a");

        Assert.True(view.Length <= maxSize, $"view is {view.Length} chars, limit {maxSize}");
    }

    [Fact]
    public void Truncate_TmpDir_NeverExceedsTheLimit()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "GlyphiteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            foreach (var maxSize in new[] { 0, 1, 5, 50, 120, 300, 1000 })
            {
                var view = ToolConfigDecorator.Truncate(new string('y', 5000), maxSize, tmpDir, "write_file", "a");
                Assert.True(view.Length <= maxSize, $"view is {view.Length} chars, limit {maxSize}");
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>`-1` means unlimited (it is the sentinel the config uses), not "truncate to nothing".</summary>
    [Fact]
    public void Truncate_NegativeLimit_IsUnlimited()
    {
        var text = new string('x', 500);

        Assert.Equal(text, ToolConfigDecorator.Truncate(text, -1, tmpDir: "", toolName: "write_file", agentId: "a"));
    }

    // ── extra_cfg: what counts as an override ──

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolConfigDecorator Decorator(
        bool peekDefault, int contentMaxSizeDefault = -1, int timeoutSecondsDefault = 0) =>
        new(new CapturingFunction(), peekDefault, contentMaxSizeDefault, timeoutSecondsDefault);

    [Fact]
    public void OverridesFrom_ValueEqualToTheDefault_IsNotAnOverride()
    {
        var overrides = Decorator(peekDefault: true).OverridesFrom(Cfg("""{"peek":true}"""));

        Assert.Empty(overrides);
    }

    [Fact]
    public void OverridesFrom_ReportsOnlyFieldsThatDifferFromDefaults()
    {
        // timeout 30 is this tool's own default, so it says nothing; the other two differ.
        var overrides = Decorator(peekDefault: true, contentMaxSizeDefault: -1, timeoutSecondsDefault: 30)
            .OverridesFrom(Cfg("""{"peek":false,"maxSize":100,"timeout":30}"""));

        Assert.Equal(2, overrides.Count);
        Assert.Equal(false, overrides["peek"]);
        Assert.Equal(100, overrides["maxSize"]);
    }

    [Fact]
    public void OverridesFrom_SentinelValues_MatchTheirDefaults()
    {
        var decorator = Decorator(peekDefault: false, contentMaxSizeDefault: -1, timeoutSecondsDefault: 0);

        Assert.Empty(decorator.OverridesFrom(Cfg("""{"maxSize":-1}""")));   // unlimited is the default
        Assert.Empty(decorator.OverridesFrom(Cfg("""{"timeout":0}""")));    // 0 means "no timeout"
        Assert.Equal(0, decorator.OverridesFrom(Cfg("""{"maxSize":0}"""))["maxSize"]);   // 0 = hide differs
        Assert.Equal(5, decorator.OverridesFrom(Cfg("""{"timeout":5}"""))["timeout"]);
    }

    [Fact]
    public void OverridesFrom_IgnoresUnknownKeysAndNonObjects()
    {
        var decorator = Decorator(peekDefault: false, contentMaxSizeDefault: 100, timeoutSecondsDefault: 0);

        Assert.Empty(decorator.OverridesFrom(Cfg("""{"whatever":1}""")));
        Assert.Empty(decorator.OverridesFrom(Cfg("\"not-an-object\"")));
        Assert.Empty(decorator.OverridesFrom(Cfg("""{"maxSize":100}""")));  // equals this tool's default
    }
}
