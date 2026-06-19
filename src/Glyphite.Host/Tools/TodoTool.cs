using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public record TodoItem(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("status")] string Status = "pending",
    [property: JsonPropertyName("priority")] string Priority = "medium"
);

public record TodoAction(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("index")] int? Index = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("priority")] string? Priority = null
);

public static class TodoTool
{
    private static readonly string[] DefaultValidStatuses = ["pending", "in_progress", "done", "cancelled", "blocked"];

    public static async Task<string> TodoWrite(
        string title,
        TodoItem[]? items,
        IMemoryStore store,
        string sessionId,
        TodoOptions opts,
        bool? peek = null)
    {
        var nextNumber = await store.GetNextNumberAsync(sessionId);
        await store.SetNextNumberAsync(sessionId, nextNumber + 1);

        items ??= [];

        var dictItems = items.Select(i => (object)new Dictionary<string, object>
        {
            ["text"] = i.Text,
            ["status"] = opts.ValidStatuses.Contains(i.Status) ? i.Status : opts.DefaultStatus,
            ["priority"] = i.Priority
        }).ToList();

        var data = new Dictionary<string, object> { ["items"] = dictItems };
        var block = MemoryBlock.Create(BlockType.todo, title, toolName: "todo_write", data: data);
        block.Number = nextNumber;

        await store.AppendBlocksAsync(sessionId, [block], nextNumber + 1);

        return FormattableString.Invariant($"Created todo list '{title}' as block {nextNumber:F1} with {items.Length} items\n") + FormatItems(dictItems);
    }

    public static async Task<string> TodoUpdate(
        double block,
        TodoAction[] actions,
        IMemoryStore store,
        string sessionId,
        TodoOptions opts,
        bool? peek = null)
    {
        var existing = await store.GetBlockAsync(sessionId, block);
        if (existing is null)
            return FormattableString.Invariant($"Block {block:F1} not found");

        if (existing.Type is not BlockType.todo and not BlockType.todo_update)
            return FormattableString.Invariant($"Block {block:F1} is not a todo or todo_update block");

        // Follow chain forward from the passed block to find the latest todo_update
        var snapshots = await store.LoadBlocksByTypeAsync(sessionId, BlockType.todo_update, null, true);
        MemoryBlock? latestSnapshot = existing.Type == BlockType.todo_update ? existing : null;
        double? chainCursor = existing.Number;
        while (chainCursor.HasValue)
        {
            var next = snapshots.FirstOrDefault(s =>
            {
                if (s.Data?.TryGetValue("parentNumber", out var raw) == true &&
                    raw is JsonElement je && je.ValueKind == JsonValueKind.Number)
                    return je.GetDouble() == chainCursor.Value;
                return s.ParentNumber == chainCursor.Value;
            });
            if (next is null) break;
            latestSnapshot = next;
            chainCursor = next.Number;
        }

        List<Dictionary<string, object?>> items;
        if (latestSnapshot is not null && latestSnapshot.Data?.TryGetValue("items", out var snapItemsObj) == true)
        {
            items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                ((JsonElement)snapItemsObj!).GetRawText()) ?? [];
        }
        else if (existing.Data?.TryGetValue("items", out var itemsObj) == true)
        {
            items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                ((JsonElement)itemsObj!).GetRawText()) ?? [];
        }
        else
        {
            return FormattableString.Invariant($"Block {block:F1} has no items");
        }

        if (items is null)
            return "Cannot parse items";

        var results = new List<string>();

        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case "set_status":
                {
                    if (action.Index is null || action.Status is null)
                    {
                        results.Add("set_status: missing index or status");
                        continue;
                    }
                    if (action.Index < 0 || action.Index >= items.Count)
                    {
                        results.Add($"set_status: index {action.Index} out of range");
                        continue;
                    }
                    if (!opts.ValidStatuses.Contains(action.Status))
                    {
                        results.Add($"set_status: invalid status '{action.Status}'");
                        continue;
                    }
                    var old = items[action.Index.Value]["status"]?.ToString() ?? "?";
                    items[action.Index.Value]["status"] = action.Status;
                    results.Add($"[{action.Index}] status {old}→{action.Status}");
                    break;
                }

                case "update":
                {
                    if (action.Index is null)
                    {
                        results.Add("update: missing index");
                        continue;
                    }
                    if (action.Index < 0 || action.Index >= items.Count)
                    {
                        results.Add($"update: index {action.Index} out of range");
                        continue;
                    }
                    var item = items[action.Index.Value];
                    if (action.Text is not null)
                    {
                        var old = item["text"]?.ToString() ?? "";
                        item["text"] = action.Text;
                        results.Add($"[{action.Index}] text changed");
                    }
                    if (action.Status is not null)
                    {
                        if (opts.ValidStatuses.Contains(action.Status))
                        {
                            var old = item["status"]?.ToString() ?? "";
                            item["status"] = action.Status;
                            results.Add($"[{action.Index}] status {old}→{action.Status}");
                        }
                        else
                        {
                            results.Add($"update: invalid status '{action.Status}'");
                        }
                    }
                    if (action.Priority is not null)
                    {
                        var old = item["priority"]?.ToString() ?? "";
                        item["priority"] = action.Priority;
                        results.Add($"[{action.Index}] priority {old}→{action.Priority}");
                    }
                    if (action.Text is null && action.Status is null && action.Priority is null)
                        results.Add($"update: no fields to update for index {action.Index}");
                    break;
                }

                case "add":
                {
                    if (string.IsNullOrWhiteSpace(action.Text))
                    {
                        results.Add("add: missing text");
                        continue;
                    }
                    var newItem = new Dictionary<string, object?>
                    {
                        ["text"] = action.Text,
                        ["status"] = opts.ValidStatuses.Contains(action.Status ?? opts.DefaultStatus) ? action.Status! : opts.DefaultStatus,
                        ["priority"] = action.Priority ?? opts.DefaultPriority
                    };

                    if (action.Index is not null && action.Index >= 0 && action.Index <= items.Count)
                    {
                        items.Insert(action.Index.Value, newItem);
                        results.Add($"add: inserted '{action.Text}' at [{action.Index}]");
                    }
                    else
                    {
                        items.Add(newItem);
                        results.Add($"add: appended '{action.Text}' at [{items.Count - 1}]");
                    }
                    break;
                }

                case "remove":
                {
                    if (action.Index is null)
                    {
                        results.Add("remove: missing index");
                        continue;
                    }
                    if (action.Index < 0 || action.Index >= items.Count)
                    {
                        results.Add($"remove: index {action.Index} out of range");
                        continue;
                    }
                    var removed = items[action.Index.Value]["text"]?.ToString() ?? "?";
                    items.RemoveAt(action.Index.Value);
                    results.Add($"remove: '{removed}' at [{action.Index}]");
                    break;
                }

                default:
                    results.Add($"unknown action type '{action.Type}'");
                    break;
            }
        }

        // Create a snapshot todo_update block (parent stays immutable)
        var snapshotItems = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(items));
        var snapshotData = new Dictionary<string, object>
        {
            ["items"] = snapshotItems!
        };
        var nextNumber = await store.GetNextNumberAsync(sessionId);
        var snapshot = MemoryBlock.Create(BlockType.todo_update, existing.Content ?? "", toolName: "todo_update", data: snapshotData);
        snapshot.Number = nextNumber;
        var parentBlock = latestSnapshot?.Number ?? existing.Number;
        snapshot.ParentNumber = parentBlock;
        snapshot.Data ??= [];
        snapshot.Data["parentNumber"] = parentBlock;
        await store.AppendBlocksAsync(sessionId, [snapshot], nextNumber + 1);

        return FormattableString.Invariant($"Updated block {existing.Number:F1}: {string.Join("; ", results)}\n") + FormatItems(items);
    }

    private static string FormatItems(IEnumerable<object> dictItems)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in dictItems)
        {
            if (item is not Dictionary<string, object> dict)
                continue;
            var text = dict.GetValueOrDefault("text")?.ToString() ?? "";
            var status = dict.GetValueOrDefault("status")?.ToString() ?? "?";
            var priority = dict.GetValueOrDefault("priority")?.ToString() ?? "";
            var statusIcon = status switch
            {
                "done" => "x",
                "in_progress" => "*",
                "blocked" => "!",
                "cancelled" => "-",
                _ => " "
            };
            sb.Append($"  [{statusIcon}] {text}");
            if (!string.IsNullOrEmpty(priority) && priority != "medium")
                sb.Append($" ({priority})");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd('\n', '\r');
    }

    private sealed class TodoInvoker(IMemoryStore store, string sessionId, IConfigService cfg)
    {
        [Description("Create a todo list block to plan and track task progress. Use this FREQUENTLY for complex/multi-step tasks to break them down and show progress. Statuses: pending, in_progress, done, cancelled, blocked. Priority: low, medium, high.")]
        public async Task<string> Write(
            [Description("Title/description of the todo list")] string title,
            [Description("Optional array of items: [{text: '...', status: 'pending', priority: 'medium'}]. Status and priority default to pending, medium.")] TodoItem[]? items,
            [Description("Auto-clean result after tool loop.")] bool? peek = null)
        {
            var opts = await cfg.GetOptionsAsync<TodoOptions>("Todo", sessionId);
            return await TodoWrite(title, items, store, sessionId, opts);
        }

        [Description("Modify a todo list block. Action types: set_status (change item status by index), update (change text/status/priority by index), add (insert new item), remove (delete item by index). Creates a snapshot block to track progress history.")]
        public async Task<string> Update(
            [Description("Block number of the todo list to modify")] double block,
            [Description("Array of action objects: {type: 'set_status'|'update'|'add'|'remove', index?: number, text?: string, status?: string, priority?: string}")] TodoAction[] actions,
            [Description("Auto-clean result after tool loop.")] bool? peek = null)
        {
            var opts = await cfg.GetOptionsAsync<TodoOptions>("Todo", sessionId);
            return await TodoUpdate(block, actions, store, sessionId, opts);
        }
    }

    public static AIFunction AsTodoWriteFunction(IMemoryStore store, string sessionId, IConfigService? cfg)
        => AIFunctionFactory.Create(
            new TodoInvoker(store, sessionId, cfg!).Write,
            "todo_write");

    public static AIFunction AsTodoUpdateFunction(IMemoryStore store, string sessionId, IConfigService? cfg)
        => AIFunctionFactory.Create(
            new TodoInvoker(store, sessionId, cfg!).Update,
            "todo_update");
}
