using System.Text.Json.Serialization;
using System.Text.Json;

namespace Glyphite.Abstractions.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlockType
{
    system_metrics,
    system_info,
    system_error,
    agent_data,
    user_message,
    agent_reasoning,
    agent_message,
    tool,
    todo,
    todo_update,
    file,
    auto_tool
}

public class MemoryBlock
{
    [JsonPropertyOrder(0)]
    public double Number { get; set; }

    [JsonPropertyOrder(1)]
    public BlockType Type { get; set; }

    [JsonPropertyOrder(2)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(2)]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyOrder(3)]
    public string Content { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(4)]
    public string? ToolName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(5)]
    public Dictionary<string, object>? Data { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(6)]
    public string? Model { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(7)]
    public double? ParentNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(8)]
    public string? ToolResult { get; set; }

    public static MemoryBlock Create(BlockType type, string content, string? toolName = null, Dictionary<string, object>? data = null, string? model = null)
        => new()
        {
            Type = type,
            Content = content,
            ToolName = toolName,
            Data = data,
            Model = model
        };

    public static MemoryBlock UserMessage(string content)
        => Create(BlockType.user_message, content);

    public static MemoryBlock AgentMessage(string content, string? model = null)
        => Create(BlockType.agent_message, content, model: model);

    public static MemoryBlock AgentReasoning(string content, string? model = null)
        => Create(BlockType.agent_reasoning, content, model: model);

    public static MemoryBlock AgentData(string key, object value)
        => Create(BlockType.agent_data, $"{key}: {value}", data: new() { [key] = value });

    public static MemoryBlock ToolCall(string toolName, string content, string? model = null)
        => Create(BlockType.tool, content, toolName: toolName, model: model);

    public static MemoryBlock AutoTool(string toolName, string content, string? result = null, string? model = null)
    {
        var block = Create(BlockType.auto_tool, content, toolName: toolName, model: model);
        block.ToolResult = result;
        return block;
    }

    public static MemoryBlock SystemInfo(string content)
        => Create(BlockType.system_info, content);

    public static MemoryBlock SystemMetrics(string content, Dictionary<string, object>? metrics = null)
        => Create(BlockType.system_metrics, content, data: metrics);

    public static MemoryBlock FileBlock(string content, string filePath)
        => Create(BlockType.file, content, data: new() { ["path"] = filePath });

    public static MemoryBlock SystemError(string content)
        => Create(BlockType.system_error, content);

    public string ToContextString()
    {
        var extra = "";

        if (ToolName is not null)
            extra += $", Tool: \"{ToolName}\"";

        if (Type == BlockType.file && Data?.TryGetValue("path", out var fp) == true)
            extra += $", Path: \"{fp}\"";

        if ((Type == BlockType.todo || Type == BlockType.todo_update) && Data?.TryGetValue("items", out var itemsObj) == true)
        {
            try
            {
                var itemsElement = (JsonElement)itemsObj;
                var items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(itemsElement.GetRawText());
                if (items is not null && items.Count > 0)
                {
                    var summary = string.Join("; ", items.Select(i => $"[{i.GetValueOrDefault("status", "?")}] {i.GetValueOrDefault("text", "")}"));
                    extra += $"\nItems: {summary}";
                }
            }
            catch { }
        }

        var body = Content;
        if (ToolResult is not null)
            body += "\n" + ToolResult;

        return $"[Block: {Number:F1}, Type: \"{Type}\"{extra}]\n{body}\n\n";
    }
}
