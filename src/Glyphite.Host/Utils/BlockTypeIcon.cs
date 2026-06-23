using System.Collections.Frozen;

namespace Glyphite.Host.Utils;

/// <summary>Maps block type names to display icons.</summary>
public static class BlockTypeIcon
{
    private static readonly FrozenDictionary<string, string> Icons = new Dictionary<string, string>
    {
        ["user_message"] = "👤",
        ["agent_message"] = "💬",
        ["agent_reasoning"] = "🧠",
        ["tool"] = "🔧",
        ["auto_tool"] = "🤖",
        ["todo"] = "📋",
        ["todo_update"] = "🔄",
        ["todo_write"] = "📋",
        ["system_info"] = "ℹ️",
        ["agent_data"] = "📁",
        ["agent_task"] = "📋",
        ["turn"] = "🔄",
    }.ToFrozenDictionary();

    public static string Get(string blockType)
        => Icons.GetValueOrDefault(blockType, "  ");
}
