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

## Session state (Jun 20)

### Latest changes — Subagent auto-create & config extraction (v0.4.58–0.4.61)

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
`Version.txt`: `0.4.61`, published up to v0.4.61