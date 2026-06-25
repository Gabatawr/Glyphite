namespace Glyphite.Host.Data;

public sealed record BlockEntity
{
    public int Id { get; set; }
    public double Number { get; set; }
    public string Type { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? UpdatedAt { get; set; }
    public string Content { get; set; } = "";
    public string? ToolName { get; set; }
    public string? Data { get; set; }
    public string? Model { get; set; }
    public string? ToolResult { get; set; }
    public bool IsCompressed { get; set; }
}

public sealed record ConfigRow(string Key, string Value, string Scope, string AgentId, string UpdatedAt);
