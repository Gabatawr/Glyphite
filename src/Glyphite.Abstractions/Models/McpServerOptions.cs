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

public class McpServersConfig
{
    public const string Section = "McpServers";
    public Dictionary<string, McpServerOptions> Servers { get; set; } = [];
    public void Validate() { }
}