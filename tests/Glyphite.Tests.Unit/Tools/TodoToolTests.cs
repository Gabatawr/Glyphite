using System.Text.Json;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

public class TodoToolTests : IDisposable
{
    private const string AgentId = "todo-test-agent";

    private readonly TestDb _db = new();

    private static readonly TodoOptions Opts = new()
    {
        ValidStatuses = ["pending", "in_progress", "done", "cancelled", "blocked"],
        DefaultStatus = "pending",
        DefaultPriority = "medium",
    };

    public void Dispose() => _db.Dispose();

    private async Task<string> Execute(string action, string? title = null, params TodoItem[] items)
    {
        await _db.Sessions.EnsureSessionAsync(AgentId);
        return await TodoTool.Execute(action, title, items, _db.Sessions, _db.Blocks, AgentId, Opts);
    }

    private async Task<List<Dictionary<string, object?>>> GetItemsAsync(string title)
    {
        var block = (await _db.Blocks.LoadBlocksAsync(AgentId)).First(b => b.Type == BlockType.todo && b.Content == title);
        var je = Assert.IsType<JsonElement>(block.Data!["items"]);
        var items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(je.GetRawText()) ?? [];
        foreach (var item in items)
            foreach (var key in item.Keys.ToList())
                item[key] = Unwrap(item[key]);
        return items;
    }

    private static object? Unwrap(object? value) => value is JsonElement je
        ? je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number => je.GetInt32(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => value,
        }
        : value;

    // ── Execute dispatch ──

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var result = await Execute("bogus");

        Assert.Contains("Unknown action 'bogus'", result);
    }

    // ── Create ──

    [Fact]
    public async Task Create_AppendsTodoBlock_WithDefaults()
    {
        var result = await Execute("create", "MyList", new TodoItem("task1"));

        Assert.Contains("MyList", result);
        Assert.Contains("task1", result);

        var block = Assert.Single(await _db.Blocks.LoadBlocksAsync(AgentId));
        Assert.Equal(BlockType.todo, block.Type);
        Assert.Equal("MyList", block.Content);
        Assert.Equal(1, block.Number);

        var items = await GetItemsAsync("MyList");
        var item = Assert.Single(items);
        Assert.Equal("task1", item["text"]);
        Assert.Equal("pending", item["status"]);
        Assert.Equal("medium", item["priority"]);
    }

    [Fact]
    public async Task Create_InvalidStatus_FallsBackToDefault()
    {
        await Execute("create", "List", new TodoItem("t", Status: "bogus"));

        var items = await GetItemsAsync("List");
        Assert.Equal("pending", items[0]["status"]);
    }

    [Fact]
    public async Task Create_ValidStatus_Preserved()
    {
        await Execute("create", "List", new TodoItem("t", Status: "in_progress"));

        var items = await GetItemsAsync("List");
        Assert.Equal("in_progress", items[0]["status"]);
    }

    [Fact]
    public async Task Create_DefaultTitle_IsTodo()
    {
        var result = await Execute("create", items: new TodoItem("t"));

        Assert.StartsWith("Todo", result);
    }

    [Fact]
    public async Task Create_AppendsSequentialNumbers()
    {
        await Execute("create", "One", new TodoItem("a"));
        await Execute("create", "Two", new TodoItem("b"));

        var blocks = (await _db.Blocks.LoadBlocksAsync(AgentId)).Where(b => b.Type == BlockType.todo).ToList();
        Assert.Equal(1, blocks[0].Number);
        Assert.Equal(2, blocks[1].Number);
    }

    // ── Update ──

    [Fact]
    public async Task Update_AddsItem_AndChangesExisting()
    {
        await Execute("create", "List", new TodoItem("existing"));

        var result = await Execute("update", "List",
            new TodoItem("new task"),
            new TodoItem("existing", Status: "done", Index: 0));

        // Return shows final state of the list
        Assert.Contains("new task", result);
        Assert.Contains("existing", result); // existing item kept, status changed

        var items = await GetItemsAsync("List");
        Assert.Equal(2, items.Count);
        Assert.Equal("done", items[0]["status"]);
        Assert.Equal("new task", items[1]["text"]);
    }

    [Fact]
    public async Task Update_MatchesItemByText_WithoutIndex()
    {
        await Execute("create", "List", new TodoItem("pick me"));

        await Execute("update", "List", new TodoItem("PICK ME", Status: "done"));

        var items = await GetItemsAsync("List");
        Assert.Equal("done", items[0]["status"]);
    }

    [Fact]
    public async Task Update_Remove_ByIndex()
    {
        await Execute("create", "List", new TodoItem("keep"), new TodoItem("remove-me"));

        var result = await Execute("update", "List", new TodoItem(Index: 1, Remove: true));

        Assert.DoesNotContain("remove-me", result);
        var items = await GetItemsAsync("List");
        var item = Assert.Single(items);
        Assert.Equal("keep", item["text"]);
    }

    [Fact]
    public async Task Update_Remove_OutOfRange_LeavesListIntact()
    {
        await Execute("create", "List", new TodoItem("only"));

        var result = await Execute("update", "List", new TodoItem(Index: 5, Remove: true));

        Assert.Contains("only", result);
        Assert.Single(await GetItemsAsync("List"));
    }

    [Fact]
    public async Task Update_InvalidStatus_NotChanged()
    {
        await Execute("create", "List", new TodoItem("t"));

        await Execute("update", "List", new TodoItem("t", Status: "bogus", Index: 0));

        var items = await GetItemsAsync("List");
        Assert.Equal("pending", items[0]["status"]);
    }

    [Fact]
    public async Task Update_TitleNotFound_ReturnsError()
    {
        var result = await Execute("update", "Missing");

        Assert.Contains("Todo list 'Missing' not found", result);
    }

    [Fact]
    public async Task Update_NoTitle_NoLists_ReturnsError()
    {
        var result = await Execute("update");

        Assert.Contains("No todo list found", result);
    }

    [Fact]
    public async Task Update_NoTitle_UsesLatestList()
    {
        await Execute("create", "First", new TodoItem("a"));
        await Execute("create", "Second", new TodoItem("b"));

        var result = await Execute("update", items: new TodoItem("c"));

        Assert.Contains("c", result);
        Assert.Single(await GetItemsAsync("First"));            // unchanged
        Assert.Equal(2, (await GetItemsAsync("Second")).Count); // latest got the item
    }

    [Fact]
    public async Task Update_AddWithoutText_LeavesListIntact()
    {
        await Execute("create", "List", new TodoItem("a"));

        await Execute("update", "List", new TodoItem());

        Assert.Single(await GetItemsAsync("List"));
    }

    // ── List ──

    [Fact]
    public async Task List_ByTitle_ReturnsFormattedItems()
    {
        await Execute("create", "List", new TodoItem("task", Status: "done"));

        var result = await Execute("list", "List");

        Assert.Contains("task", result);
        Assert.Contains("[x]", result);
    }

    [Fact]
    public async Task List_ByTitle_NotFound_ReturnsError()
    {
        var result = await Execute("list", "Missing");

        Assert.Contains("Todo list 'Missing' not found", result);
    }

    [Fact]
    public async Task List_All_ShowsCounts()
    {
        await Execute("create", "A", new TodoItem("a1"));
        await Execute("create", "B", new TodoItem("b1", Status: "done"));

        var result = await Execute("list");

        Assert.Contains("Todo Lists (2)", result);
        Assert.Contains("A  (0/1 done)", result);
        Assert.Contains("B  (1/1 done)", result);
    }

    [Fact]
    public async Task List_NoLists_ReturnsError()
    {
        var result = await Execute("list");

        Assert.Contains("No todo lists found", result);
    }
}
