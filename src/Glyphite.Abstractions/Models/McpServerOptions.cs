namespace Glyphite.Abstractions.Models;

public class McpServerOptions
{
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "stdio";
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = [];
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Per‑server/tool execution settings for MCP tools (mirrors <see cref="ToolExecutionEntry"/>).
/// Array format. Use <c>"Mcp": "*"</c> for all‑server defaults, <c>"Tool": "*"</c> for all‑tool overrides.
/// Resolution merges hierarchically: wildcard server → exact server → wildcard tool → exact tool.
/// </summary>
public class McpExecutionEntry
{
    /// <summary>Server name, or <c>"*"</c> for all servers.</summary>
    public string Mcp { get; set; } = "";
    /// <summary>Optional tool name (before server prefix). <c>"*"</c> matches all tools from this server.</summary>
    public string? Tool { get; set; }
    public McpExecutionOptionsEntry? Options { get; set; }
}

/// <summary>
/// Execution options for MCP tools. Nullable fields allow hierarchical merging —
/// <c>null</c> means "inherit from lower‑priority entry or use the shared <see cref="Glyphite.Host.Tools.ToolExecutionDefaults"/>".
/// </summary>
public class McpExecutionOptionsEntry
{
    /// <summary>Timeout in seconds for MCP tool execution. <c>null</c> = inherit.</summary>
    public int? Timeout { get; set; }
    /// <summary>Max result size in characters. <c>null</c> = inherit.</summary>
    public int? MaxSize { get; set; }
    /// <summary>Auto‑clean result after tool loop. <c>null</c> = inherit.</summary>
    public bool? Peek { get; set; }
}

public class McpServersConfig
{
    public const string Section = "McpServers";
    public Dictionary<string, McpServerOptions> Servers { get; set; } = [];
    public McpExecutionEntry[] McpExecution { get; set; } = [];
    public void Validate() { }
}
