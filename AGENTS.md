# Agents' workflow

We work from the **published** version of the application — the single-file binary in `~/.glyphite/`.

**I (the agent) build** — I run `dotnet build` and fix any compilation errors.

**You (the user) publish** — when the build is green, you run `./publish.sh` which creates the single-file binary, backs up the previous version, and updates `~/.glyphite/glyphite`.

After publish — exit and restart glyphite to test the new build.

## Configuration hierarchy

Settings are applied in this order (each overrides the previous):

1. **`appsettings.json`** (embedded in the binary) — base defaults. **Do not modify** — it's compiled into the binary.
2. **`Glyphite.json`** in current working directory — overrides base defaults. Use this for your own preferences (e.g. `"patch_file": -1` to always show diffs).
3. **`Glyphite.{agentName}.json`** in current working directory — agent-specific overrides (highest priority).

## Session state (Oct 24)

### New features (since Jun 18)
- **`ParentNumber` + `Data["parentNumber"]`** — every `file` block stores `Data["parentNumber"]` referencing its parent `tool` block. `todo_update` blocks form a forward chain via `Data["parentNumber"]`.
- **`idx_blocks_agent_deleted` index** — `CREATE INDEX IF NOT EXISTS idx_blocks_agent_deleted ON blocks(agent_id, is_deleted)` — speeds up all context load / peek cleanup queries.
- **`memory delete/recover cascade`** — `memory delete blocks=[N]` cascades by default (`cascade=true`) through `Data["parentNumber"]` chain. `memory recover` defaults to `cascade=false`. Both configurable via `cascade` parameter.
- **Todo chain** — each `todo_update` snapshots the **previous** snapshot (not root). Deleting a mid-chain block cascades backward. Forward chain traversal finds the latest snapshot from any point.
- **`DeleteFileBlocksCascadeParentToolByPathAsync`** — deletes file blocks + their parent tool blocks by path. Uses `ParentNumber` instead of fragile `.LastOrDefault()`.

### Architecture
- **Abstractions** — interfaces, models, no deps (except `Microsoft.Extensions.AI`)
- **Host** — service implementations (TurnProcessor, FailSafeChatClient, MemoryStore, BlockMemoryProvider), tools, MCP, DI wiring
- **Cli** — UI only (ChatRepl + 3 partials, ConsoleRenderer). No persistence logic.

### Peek flow (updated)
- Inter-iteration cleanup (`RemovePeekBlocksAsync(includeReasoning: false)`) cleans tool/file peek blocks only
- Start-of-turn cleanup (`RemovePeekBlocksAsync(includeReasoning: true)`) cleans ALL peek blocks (safety net)
- Tools affected: `read_file`, `write_file`, `patch_file`, `fetch_web`, all others

### Schema

```sql
-- Index (added Oct 24):
CREATE INDEX IF NOT EXISTS idx_blocks_agent_deleted ON blocks(agent_id, is_deleted);

-- blocks table columns:
-- id, agent_id, number, type, created_at, content, tool_name, data, model,
-- tool_result, updated_at, parent_number, is_deleted
```

See `MemoryStore.cs` `Initialize()` for full DDL.

### Version
`Version.txt`: `0.3.61`, published up to v0.3.61
