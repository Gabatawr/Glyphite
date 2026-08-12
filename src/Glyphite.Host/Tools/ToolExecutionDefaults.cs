namespace Glyphite.Host.Tools;

/// <summary>
/// Single source of truth for per-tool execution defaults (peek, maxSize, timeout).
/// Per-tool overrides come from <c>Glyphite:ToolExecution</c> (builtin tools) or
/// <c>Glyphite:McpExecution</c> (MCP tools); these constants are the fallback when
/// a tool has no config entry.
/// </summary>
internal static class ToolExecutionDefaults
{
    /// <summary>Default auto-clean behavior for tool results (off).</summary>
    public const bool Peek = false;

    /// <summary>Default maximum result size in characters.</summary>
    public const int ContentMaxSize = 100_000;

    /// <summary>Default timeout for builtin tools, in seconds.</summary>
    public const int BuiltinTimeoutSeconds = 120;

    /// <summary>Default timeout for MCP tools, in seconds — remote calls cross process boundaries.</summary>
    public const int McpTimeoutSeconds = 300;
}
