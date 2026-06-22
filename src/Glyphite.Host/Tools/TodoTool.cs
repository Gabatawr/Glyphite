using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public record TodoItem(
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("priority")] string? Priority = null,
    [property: JsonPropertyName("index")] int? Index = null,
    [property: JsonPropertyName("remove")] bool? Remove = null
);

public static class TodoTool
{
    private static readonly string[] DefaultValidStatuses = ["pending", "in_progress", "done", "cancelled", "blocked"];

    public static async Task<string> Execute(
        string action,
        string? title,
        TodoItem[]? items,
        IAgentStore agentStore,
        IBlockStore blockStore,
        string sessionId,
        TodoOptions opts)
    {
        switch (action)
        {
            case "create":
                return await Create(title, items, agentStore, blockStore, sessionId, opts);
            case "update":
                return await Update(title, items, agentStore, blockStore, sessionId, opts);
            default:
                return $"Unknown action '{action}'. Use 'create' or 'update'.";
        }
    }

    private static async Task<string> Create(
        string? title,
        TodoItem[]? items,
        IAgentStore agentStore,
        IBlockStore blockStore,
        string sessionId,
        TodoOptions opts)
    {
        title ??= "Todo";
        var nextNumber = await agentStore.GetNextNumberAsync(sessionId);
        await agentStore.SetNextNumberAsync(sessionId, nextNumber + 1);

        items ??= [];

        var dictItems = items.Select(i => (object)new Dictionary<string, object>
        {
            ["text"] = i.Text ?? "",
            ["status"] = opts.ValidStatuses.Contains(i.Status ?? opts.DefaultStatus) ? i.Status! : opts.DefaultStatus,
            ["priority"] = i.Priority ?? opts.DefaultPriority
        }).ToList();

        var data = new Dictionary<string, object> { ["items"] = dictItems };
        var block = MemoryBlock.Create(BlockType.todo, title, toolName: "todo", data: data);
        block.Number = nextNumber;

        await blockStore.AppendBlocksAsync(sessionId, [block], nextNumber + 1);

        return title + "\n" + FormatItems(dictItems);
    }

    private static async Task<string> Update(
        string? title,
        TodoItem[]? items,
        IAgentStore agentStore,
        IBlockStore blockStore,
        string sessionId,
        TodoOptions opts)
    {
        // Auto-find the latest todo block
        var todos = await blockStore.LoadBlocksByTypeAsync(sessionId, BlockType.todo, 1, true);
        if (todos.Count == 0)
            return "No todo list found. Create one with action='create' first.";

        var existing = todos[0];

        List<Dictionary<string, object?>> currentItems;
        if (existing.Data?.TryGetValue("items", out var itemsObj) == true)
        {
            currentItems = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                ((JsonElement)itemsObj!).GetRawText()) ?? [];
        }
        else
        {
            return "Latest todo list has no items";
        }

        if (currentItems is null)
            return "Cannot parse items";

        items ??= [];
        var results = new List<string>();

        foreach (var item in items)
        {
            if (item.Remove == true)
            {
                // Remove by index
                if (item.Index is null)
                {
                    results.Add("remove: missing index");
                    continue;
                }
                if (item.Index < 0 || item.Index >= currentItems.Count)
                {
                    results.Add($"remove: index {item.Index} out of range");
                    continue;
                }
                var removed = currentItems[item.Index.Value]["text"]?.ToString() ?? "?";
                currentItems.RemoveAt(item.Index.Value);
                results.Add($"remove: '{removed}' at [{item.Index}]");
            }
            else if (item.Index is not null)
            {
                // Update existing item by index
                if (item.Index < 0 || item.Index >= currentItems.Count)
                {
                    results.Add($"update: index {item.Index} out of range");
                    continue;
                }
                var target = currentItems[item.Index.Value];
                var changed = false;

                if (item.Text is not null)
                {
                    target["text"] = item.Text;
                    results.Add($"[{item.Index}] text changed");
                    changed = true;
                }
                if (item.Status is not null)
                {
                    if (opts.ValidStatuses.Contains(item.Status))
                    {
                        var old = target["status"]?.ToString() ?? "?";
                        target["status"] = item.Status;
                        results.Add($"[{item.Index}] status {old}→{item.Status}");
                        changed = true;
                    }
                    else
                    {
                        results.Add($"update: invalid status '{item.Status}'");
                    }
                }
                if (item.Priority is not null)
                {
                    var old = target["priority"]?.ToString() ?? "?";
                    target["priority"] = item.Priority;
                    results.Add($"[{item.Index}] priority {old}→{item.Priority}");
                    changed = true;
                }
                if (!changed)
                    results.Add($"update: no fields to change for index {item.Index}");
            }
            else
            {
                // No index — add new item
                if (string.IsNullOrWhiteSpace(item.Text))
                {
                    results.Add("add: missing text");
                    continue;
                }
                var newItem = new Dictionary<string, object?>
                {
                    ["text"] = item.Text,
                    ["status"] = opts.ValidStatuses.Contains(item.Status ?? opts.DefaultStatus) ? item.Status! : opts.DefaultStatus,
                    ["priority"] = item.Priority ?? opts.DefaultPriority
                };
                currentItems.Add(newItem);
                results.Add($"add: appended '{item.Text}' at [{currentItems.Count - 1}]");
            }
        }

        // Update the existing todo block in-place
        var updatedData = new Dictionary<string, object>
        {
            ["items"] = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(currentItems))!
        };
        await blockStore.UpdateBlockDataAsync(sessionId, existing.Number, updatedData);

        // Update title if provided
        if (!string.IsNullOrWhiteSpace(title) && title != existing.Content)
        {
            await blockStore.UpdateBlockAsync(sessionId, existing.Number, content: title);
        }

        var displayTitle = !string.IsNullOrWhiteSpace(title) ? title : existing.Content ?? "Updated";
        return displayTitle + "\n" + FormatItems(currentItems);
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

    private sealed class TodoInvoker(IAgentStore agentStore, IBlockStore blockStore, string sessionId, IConfigService cfg)
    {
        [Description("Create or update a todo list block to plan and track task progress. Only ONE todo list is active at a time — 'update' always modifies the latest created list. Statuses: pending, in_progress, done, cancelled, blocked. Priority: low, medium, high.")]
        public async Task<string> Invoke(
            [Description("Action: 'create' to create a new todo list, 'update' to modify the latest todo list (there is only one active list — update always targets the most recently created one)")] string action,
            [Description("Title/description (required for create, optional for update — updates the title of the LATEST todo list; does NOT search by title, only renames the latest list)")] string? title = null,
            [Description("Items for the todo. For create: {text: string, status?: string, priority?: string}. For update: {index?: number, text?: string, status?: string, priority?: string, remove?: boolean}. Items without index are added; items with index are updated; items with remove=true are deleted.")] TodoItem[]? items = null)
        {
            var opts = await cfg.GetOptionsAsync<TodoOptions>(TodoOptions.Section, sessionId);
            return await Execute(action, title, items, agentStore, blockStore, sessionId, opts);
        }
    }

    public static AIFunction AsTodoFunction(IAgentStore agentStore, IBlockStore blockStore, string sessionId, IConfigService cfg)
        => AIFunctionFactory.Create(
            new TodoInvoker(agentStore, blockStore, sessionId, cfg).Invoke,
            "todo");
}
