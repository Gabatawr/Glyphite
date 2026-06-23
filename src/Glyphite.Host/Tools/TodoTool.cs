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
            case "list":
                return await List(title, blockStore, sessionId);
            default:
                return $"Unknown action '{action}'. Use 'create', 'list', or 'update'.";
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

        var dictItems = items.Select(i => new Dictionary<string, object?>
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
        // Find by title (ID), fall back to latest if title is null
        var existing = await FindTodoByTitle(title, blockStore, sessionId);
        if (existing is null)
        {
            if (!string.IsNullOrEmpty(title))
                return $"Todo list '{title}' not found. Create one with action='create' first.";
            var todos = await blockStore.LoadBlocksByTypeAsync(sessionId, BlockType.todo, 1, true);
            if (todos.Count == 0)
                return "No todo list found. Create one with action='create' first.";
            existing = todos[0];
        }

        var currentItems = DeserializeItems(existing);

        items ??= [];
        var results = new List<string>();

        foreach (var item in items)
        {
            if (item.Remove == true)
            {
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
                continue;
            }

            // Resolve target index: by explicit index, or by matching text
            int? targetIdx = item.Index;
            if (targetIdx is null && !string.IsNullOrWhiteSpace(item.Text))
            {
                // Try to find existing item with matching text (case-insensitive)
                var match = currentItems
                    .Select((dict, idx) => (dict, idx))
                    .FirstOrDefault(x => string.Equals(x.dict["text"]?.ToString(), item.Text, StringComparison.OrdinalIgnoreCase));
                if (match.dict is not null)
                    targetIdx = match.idx;
            }

            if (targetIdx is not null)
            {
                if (targetIdx < 0 || targetIdx >= currentItems.Count)
                {
                    results.Add($"update: index {targetIdx} out of range");
                    continue;
                }
                var target = currentItems[targetIdx.Value];
                var changed = false;

                if (item.Text is not null)
                {
                    target["text"] = item.Text;
                    results.Add($"[{targetIdx}] text changed");
                    changed = true;
                }
                if (item.Status is not null)
                {
                    if (opts.ValidStatuses.Contains(item.Status))
                    {
                        var old = target["status"]?.ToString() ?? "?";
                        target["status"] = item.Status;
                        results.Add($"[{targetIdx}] status {old}→{item.Status}");
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
                    results.Add($"[{targetIdx}] priority {old}→{item.Priority}");
                    changed = true;
                }
                if (!changed)
                    results.Add($"update: no fields to change for index {targetIdx}");
            }
            else
            {
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

        // Update in-place — serialize items as JsonElement (avoids round-trip through object)
        var itemsElement = JsonSerializer.SerializeToElement(currentItems);
        var updatedData = new Dictionary<string, object> { ["items"] = itemsElement };
        await blockStore.UpdateBlockDataAsync(sessionId, existing.Number, updatedData);

        var displayTitle = existing.Content ?? "Updated";
        return displayTitle + "\n" + FormatItems(currentItems);
    }

    private static async Task<string> List(
        string? title,
        IBlockStore blockStore,
        string sessionId)
    {
        // Show specific list by title
        if (!string.IsNullOrEmpty(title))
        {
            var existing = await FindTodoByTitle(title, blockStore, sessionId);
            if (existing is null)
                return $"Todo list '{title}' not found. Create one with action='create' first.";
            var items = DeserializeItems(existing);
            return title + "\n" + FormatItems(items);
        }

        // No title — show all lists
        var all = await blockStore.LoadBlocksByTypeAsync(sessionId, BlockType.todo, null, false);
        if (all.Count == 0)
            return "No todo lists found. Create one with action='create' first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"── Todo Lists ({all.Count}) ──────────────────");
        foreach (var b in all)
        {
            var items = DeserializeItems(b);
            var done = items.Count(i => i.GetValueOrDefault("status")?.ToString() == "done");
            sb.AppendLine($"  {b.Content}  ({done}/{items.Count} done)");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Find a todo block by its title (Content field). Case-insensitive.</summary>
    private static async Task<MemoryBlock?> FindTodoByTitle(string? title, IBlockStore blockStore, string sessionId)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var all = await blockStore.LoadBlocksByTypeAsync(sessionId, BlockType.todo, null, false);
        return all.FirstOrDefault(b => string.Equals(b.Content, title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deserialize items from a todo block's Data["items"]. Returns empty list on failure.</summary>
    private static List<Dictionary<string, object?>> DeserializeItems(MemoryBlock block)
    {
        if (block.Data?.TryGetValue("items", out var itemsObj) == true && itemsObj is JsonElement je)
            return JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(je.GetRawText()) ?? [];
        return [];
    }

    private static string FormatItems(List<Dictionary<string, object?>> dictItems)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var dict in dictItems)
        {
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
        [Description("Create, update, or list a todo list to plan and track task progress. Each todo list is identified by its title (ID). 'create' makes a new list; 'update' modifies a specific list by title; 'list' shows a list by title (or all lists if title omitted). Statuses: pending, in_progress, done, cancelled, blocked. Priority: low, medium, high.")]
        public async Task<string> Invoke(
            [Description("Action: 'create' to create a new todo list (title as ID), 'update' to modify an existing list by title, 'list' to show a list by title (or all if omitted)")] string action,
            [Description("Title (ID) for the todo list. Required for create. For update: finds the list to modify. For list: which list to show. Omit for update/list to affect the latest list or show all.")] string? title = null,
            [Description("Items for the todo. For create: {text: string, status?: string, priority?: string}. For update: {index?: number, text?: string, status?: string, priority?: string, remove?: boolean}. Items without index are added; items with index are updated; items with remove=true are deleted. Not needed for 'list'.")] TodoItem[]? items = null)
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
