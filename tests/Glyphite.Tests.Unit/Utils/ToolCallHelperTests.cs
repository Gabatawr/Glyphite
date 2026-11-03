using System.Text.Json;
using Glyphite.Host.Tools;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

/// <summary>
/// Which tool results are peek — shown to the model once, then cleaned out of the history. The
/// precedence matters: <c>extra_cfg.peek</c> (model, per call) beats the legacy flat <c>peek</c>,
/// which beats the tool's configured default, which beats the name-based fallback.
/// </summary>
public class ToolCallHelperTests
{
    private static ToolConfigDecorator Decorator(bool peekDefault) => new(
        AIFunctionFactory.Create(() => "ok", "capture"),
        peekDefault,
        contentMaxSizeDefault: -1,
        timeoutSecondsDefault: 0);

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] items)
        => items.ToDictionary(i => i.Key, i => i.Value);

    [Fact]
    public void WriteFile_IsPeekByDefault()
        => Assert.True(ToolCallHelper.IsPeekCall((AIFunction?)null, "write_file", null));

    [Fact]
    public void PatchFile_IsPeekByDefault()
        => Assert.True(ToolCallHelper.IsPeekCall((AIFunction?)null, "patch_file", null));

    [Fact]
    public void Bash_IsNotPeekByDefault()
        => Assert.False(ToolCallHelper.IsPeekCall((AIFunction?)null, "bash", null));

    [Fact]
    public void LegacyFlatPeek_IsHonoured()
        => Assert.True(ToolCallHelper.IsPeekCall(
            (AIFunction?)null, "bash", Args(("peek", true))));

    [Fact]
    public void ExtraCfgPeek_BeatsTheToolDefault()
        => Assert.False(ToolCallHelper.IsPeekCall(
            Decorator(peekDefault: true), "bash",
            Args(("extra_cfg", Args(("peek", false))))));

    [Fact]
    public void ExtraCfgPeek_BeatsTheLegacyFlatArgument()
        => Assert.False(ToolCallHelper.IsPeekCall(
            (AIFunction?)null, "bash",
            Args(("peek", true), ("extra_cfg", Args(("peek", false))))));

    [Fact]
    public void ConfiguredDefault_BeatsTheNameBasedFallback()
        => Assert.True(ToolCallHelper.IsPeekCall(Decorator(peekDefault: true), "bash", null));

    /// <summary>The model's arguments arrive as JSON, so the same object can be a <see cref="JsonElement"/>.</summary>
    [Fact]
    public void ExtraCfgAsJsonElement_IsUnderstood()
        => Assert.True(ToolCallHelper.IsPeekCall(
            (AIFunction?)null, "bash",
            Args(("extra_cfg", JsonDocument.Parse("""{"peek":true}""").RootElement))));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1.0K")]
    [InlineData(133_200, "133.2K")]
    public void FormatK_UsesARawNumberBelowThousand(long value, string expected)
        => Assert.Equal(expected, ToolCallHelper.FormatK(value));
}
