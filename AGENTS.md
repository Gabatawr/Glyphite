# Agents' workflow

We work from the **published** version of the application — the single-file binary in `~/.glyphite/`.

**I (the agent) build** — I run `dotnet build` and fix any compilation errors.

**You (the user) publish** — when the build is green, you run `./publish.sh` which creates the single-file binary, backs up the previous version, and updates `~/.glyphite/glyphite`.

After publish — exit and restart glyphite to test the new build.

> **Important:** never modify `appsettings.json` (embedded in binary). Changes to it require a rebuild + republish. Use `Glyphite.json` for overrides.

## Configuration hierarchy

Settings are applied in this order (each overrides the previous):

1. **`appsettings.json`** (embedded in the binary) — base defaults. **Do not modify** — it's compiled into the binary.
2. **`Glyphite.json`** in current working directory — overrides base defaults. Use this for your own preferences (e.g. `"patch_file": -1` to always show diffs).
3. **`Glyphite.{agentName}.json`** in current working directory — agent-specific overrides (highest priority).

## Session state (Jul 24)

### Latest changes — Serilog logging, atomic compaction, parallel summarization (v0.7.0–0.7.6)

**1. Serilog + ILogger\<T\> — structured file logging:**

Replaced all `Console.Error.WriteLine` / `Console.WriteLine` in Host services with `ILogger<T>` or `Serilog.Log`:

```csharp
// Before
catch { Console.Error.WriteLine("[TurnProcessor] Failed to parse args for path"); }

// After
catch { _logger.LogWarning("Failed to parse args for path"); }
```

- **Path:** `~/.glyphite/logs/{dd-MM-yyyy}-{run}.log`
- **Level:** Information+ (turn start/end, compaction, MCP events, tool iterations)
- **Subagent isolation:** subagents write to the same log file but **never to console** — no UI pollution
- `Bootstrapper.cs`: `Log.Logger` configured with `WriteTo.File(...)`, `.UseSerilog()` in Host builder
- NuGet deps: `Serilog 4.2.*`, `Serilog.Sinks.File 6.0.*`, `Serilog.Extensions.Hosting 9.0.*`

Services migrated (15 replacements total):

| Service | Changes |
|---------|---------|
| `TurnProcessor` | 4× `Console.Error` → `_logger.LogWarning`, added `_logger.LogInformation` for turn start/end |
| `CompactionService` | 1× `Console.Error` → `_logger.LogWarning`, added start/end Information logs |
| `McpService` | 6× `Console.Error` + 1× `Console.WriteLine` → `_logger.LogWarning/LogInformation` |
| `FailSafeChatClient` | 1× `Console.Error` → `_logger.LogWarning`, added iteration count log |
| `BashSession` | 2× `Console.Error` → `Serilog.Log.Warning` |
| `ConfigService` | 1× `Console.WriteLine` → `_logger.LogInformation` (`LogAction` preserved) |

**2. Atomic compaction via `ReplaceBlocksSinceAsync`:**

Before: three separate DB calls (`DeleteBlocksSinceAsync` + `RemoveBlocksAsync` + `AppendBlocksAsync`) — crash between them = **data loss**.

After: single `ReplaceBlocksSinceAsync` in one SQLite transaction:

```csharp
await using var tx = await _conn.BeginTransactionAsync();
// 1. Soft-delete individual blocks (old zone unprotected blocks)
// 2. Hard-delete everything from cutoff point
// 3. Insert new blocks (summaries + preserved)
// 4. Update next_number
await tx.CommitAsync();
```

- `IBlockStore.ReplaceBlocksSinceAsync(agentId, fromNumber, newBlocks, nextNumber, softDeleteNums)`
- On crash — SQLite rollback, all data intact. Protected blocks from failed summarization are preserved via `summarizedFallback`.

**3. Parallel zone summarization:**

Old zones (3+) are now summarized **in parallel** via `Task.WhenAll` instead of sequentially:

```csharp
// Fan-out: all zones start simultaneously
var zoneTasks = new List<(List<MemoryBlock> zone, Task<string?> task)>();
foreach (var zone in zoneProtectedBlocks)
    zoneTasks.Add((zone, SummarizeSingleZoneAsync(sessionId, zone, model)));

// Fan-in: collect results
foreach (var (zone, task) in zoneTasks)
{
    var summary = await task;
    ...
}
```

Before: 6 zones = 6× sequential LLM latency (~12s). After: 6 zones ≈ latency of the slowest one (~2s).

**4. Compaction usage tracking from response:**

Before: compaction LLM calls were **invisible** in session usage stats.

After: `SummarizeSingleZoneAsync` parses `response.RawRepresentation` for `Usage.InputTokenCount`, `CachedTokenCount`, `OutputTokenCount` and records directly to session:

```csharp
var response = await _chatClient.GetResponseAsync(messages, chatOpts);
// ... extract Usage from response.RawRepresentation ...
if (hit > 0 || miss > 0 || output > 0)
    await _agentStore.RecordUsageAsync(sessionId, hit, miss, output, model: model);
```

Same pattern as SubAgentTool — real token costs from actual API response.

**5. `peek_clean` → `peek_reasoning`:**

Renamed the auto-tool block for clarity:

| Aspect | Before | After |
|--------|--------|-------|
| auto_tool name | `peek_clean` | `peek_reasoning` |
| LLM description | "peek blocks cleaned from previous turn" | "peek reasoning blocks cleaned from previous turn" |
| Config key | `peek_clean: 0` | `peek_reasoning: 0` |

Impact: `TurnProcessor.cs`, `system-prompt.md`, `Glyphite.json`, `appsettings.json`.

**6. `[AutoTool: compression]` notification:**

When compaction triggers (threshold exceeded), an auto-tool block is shown **before** the LLM summarization call, explaining the delay:

```
[AutoTool: compression | {"AutoCompress":true,"AutoThreshold":75}]
```

Only shown when compaction actually runs — not every turn.

**7. No hardcoded Temperature/MaxOutputTokens:**

```csharp
// Before
var chatOpts = new ChatOptions
{
    ModelId = model,
    Temperature = 0.3f,       // ← hardcoded
    MaxOutputTokens = 512      // ← hardcoded
};

// After
var chatOpts = new ChatOptions
{
    ModelId = model            // ← model defaults only
};
```

Removed from both `CompactionService.SummarizeSingleZoneAsync` and `SubAgentTool.RunAgentTask`.

### Previous — Parallel tool execution & subagent refactor (v0.4.62+)

**1. Parallel tool execution (`FailSafeChatClient.cs`):**

Added `BuildToolGroups` — groups consecutive parallel-safe tool calls into batches for concurrent execution:

```csharp
// Parallel-safe tools:
"read_file", "fetch_web", "search_glob", "search_grep", "subagent_use", "subagent_run"
```

- Tools with `mode="parallel"` and same agent `name` are **split into separate groups** (sequential), preventing race conditions
- Tools with `mode="sequential"` (default) or no mode are executed one at a time
- Parallel batch uses `Task.WhenAll` — all tasks start, results yield as each completes

**2. `SubAgentManager.RunAsync` — SemaphoreSlim added:**

Each `AgentScopeEntry` now has a `SemaphoreSlim(1,1)`. `RunAsync` acquires it before execution and releases after, protecting against concurrent access to the same agent scope.

```csharp
await entry.Semaphore.WaitAsync();
try { return await Task.Run(async () => await runFunc(entry.Scope)); }
finally { entry.Semaphore.Release(); }
```

**3. `subagent_run` — three modes (replaces `saveMemory` logic):**

| Scenario | Behavior |
|---|---|
| no name (auto-GUID) | Creates temp agent → runs → **deletes entirely** |
| name + agent doesn't exist | Creates temp agent with config → runs → **deletes entirely** |
| name + agent exists | **Dry-run**: runs → cleans **only delta** blocks/usage. Existing memory preserved |

Used with `mode="parallel"` for concurrent one-shot tasks.

**4. `subagent_use` — always persistent, always auto-creates:**

- `saveMemory` parameter **removed** — now **always** preserves memory and context
- If agent doesn't exist — **auto-creates** (no need for `saveMemory=true` flag)
- `memory` tool always available for subagents created via `subagent_use`
- Used with `mode="parallel"` to delegate concurrent work to named agents

**5. Tool streaming config (`appsettings.json`):**

Added `subagent_run` and `subagent_use` to `ToolStreaming:ToolMaxLength` (default: `-1` = full output). Can be set to `0` (hidden) or `N` (first N chars).

### Previous — Subagent auto-create & config extraction (v0.4.58–0.4.61)

**Problem:** `subagent_use saveMemory=true` couldn't be used on non-existing agents — there was no tool to create a persistent subagent. `subagent_run` always deleted the agent after execution. Also, config loading logic was mixed into the static `SubAgentTool` class, making it untestable and hard to reuse.

**Changes in `SubAgentTool.cs`:**

1. **Auto-create on `subagent_use saveMemory=true`** (lines 227–245):
   - If agent exists → proceeds as before
   - If agent doesn't exist AND `saveMemory=true` → validates name, calls `CreateAgentAsync`, loads config, then executes
   - If agent doesn't exist AND `saveMemory=false` → returns error with hint to use `saveMemory=true` or `subagent_run`
   - `IAgentManager` and `ISubAgentConfigLoader` injected via DI

2. **Config loading extracted** (lines 80–144 removed):
   - `LoadSubAgentConfigAsync`, `ReadAndFlattenConfigFileAsync`, `FlattenJsonElement` moved to `SubAgentConfigLoader` service
   - New interface: `ISubAgentConfigLoader` in `Glyphite.Abstractions`
   - New implementation: `SubAgentConfigLoader` in `Glyphite.Host.Services`
   - Registered as singleton in DI (`HostServiceCollectionExtensions.cs`)
   - `SubAgentTool.cs` slimmed from 350 to 284 lines (-66 lines)

**Dead code removed:**
- `ToolFactory.cs` (32 lines) — unreferenced legacy factory, deleted

### Previous — Peek & MessageList cleanup (v0.4.40–0.4.48)

**Problem:** `TurnProcessor.ProcessUpdate` was cleaning `contextMessages` (dead code — never read after line 81). The actual LLM-visible data was in `FailSafeChatClient.messageList`, which was **never cleaned**. Peek results accumulated and were visible to LLM on every iteration.

**Fix in `FailSafeChatClient.cs`:**
Two independent cleanup mechanisms, both running **after LLM consumes the result** (after streaming loop, before `hasToolCall` check):

1. **Peek cleanup** (lines 133–149):
   - Tracks `_pendingPeekCallIds` (callId of tools with `peek=true`)
   - After LLM generates response → replaces peek results with `"(peek)"` in `messageList`
   - Keeps `FunctionResultContent` (same callId) so API doesn't reject unmatched tool_calls
   - LLM sees the real result **exactly once**, then sees `"(peek)"` on subsequent iterations

2. **Memory clean cleanup** (lines 151–167):
   - Tracks `_pendingMemoryCleanBlocks` (block numbers from `memory clean` result)
   - After LLM generates response → removes the corresponding `[Block: N, ...]` messages from `messageList`
   - Works alongside `TurnProcessor.DeleteBlocksAsync` (which deletes from DB)

**Two independent cleanup paths:**

| Path | What it cleans | When |
|------|---------------|------|
| **TurnProcessor** (DB) | `tool_result` in SQLite (skips saving for peek), deletes `is_deleted=1` | During tool execution (after FunctionResultContent) |
| **FailSafeClient** (messageList) | `ChatRole.Tool` messages with peek results → truncate to `"(peek)"`; deleted block messages → remove from list | After LLM consumes the result (next iteration start) |

**Important:** The `contextMessages` modifications in `TurnProcessor.cs:228–271` (memory clean + peek replace) are now **dead code** — `contextMessages` is never read after being copied to `initialMessages` at line 81. All actual cleanup happens in `FailSafeClient` on `messageList`.

### Architecture
- **Abstractions** — interfaces, models, no deps (except `Microsoft.Extensions.AI`)
  - Includes `ISubAgentConfigLoader`, `IAgentManager`, `IMemoryStore`, etc.
- **Host** — service implementations (TurnProcessor, FailSafeChatClient, MemoryStore, BlockMemoryProvider, SubAgentConfigLoader, SubAgentManager), tools (SubAgentTool, ToolRegistry, etc.), MCP, DI wiring
- **Cli** — UI only (ChatRepl + 3 partials, ConsoleRenderer). No persistence logic.

### Peek flow
Two levels of peek cleanup, both in `MemoryStore.Blocks.cs`:

1. **Inter-iteration** (`ClearPeekMarkersAsync(includeReasoning: false)`) — cleans tool/file peek blocks between tool batches (`RemovePeekBlocksAsync` + set `tool_result = NULL`).
2. **Start-of-turn** (`RemovePeekBlocksAsync(includeReasoning: true)`) — cleans ALL peek blocks from DB (safety net before new turn).

Both are separate from the `FailSafeClient` messageList cleanup — DB and in-memory are independent.

### Schema

```sql
-- Index:
CREATE INDEX IF NOT EXISTS idx_blocks_agent_deleted ON blocks(agent_id, is_deleted);

-- blocks table columns:
-- id, agent_id, number, type, created_at, content, tool_name, data, model,
-- tool_result, updated_at, parent_number, is_deleted
```

See `MemoryStore.cs` `Initialize()` for full DDL.

### Version
`Version.txt`: `0.7.6`, published up to v0.7.6
