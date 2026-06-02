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

## Session state (Jun 18)

### Architecture
- **Abstractions** — interfaces, models, no deps
- **Host** — service implementations (TurnProcessor, FailSafeChatClient, MemoryStore)
- **Cli** — UI only (ChatRepl, ConsoleRenderer). No persistence logic.

### Config flow
`appsettings.json` (embedded) + `Glyphite.json` (cwd) + `Glyphite.{agent}.json` (cwd, per-agent overrides)
→ `InitializeAsync()` seeds into SQLite DB (config table) → `IConfigService.GetOptionsAsync<T>(section)` reads fresh per-tool-call.

### Peek flow
- LLM calls tool with `"peek": true` → tool call block created with `Data["peek"] = true`
- File blocks (read_file/write_file) also get `Data["peek"] = true` if peek
- Tool result NOT saved to block (`UpdateBlockToolResultAsync` skipped for isPeek)
- Within-turn iterations (tool→LLM→tool): LLM sees full result via `messageList` in FailSafeChatClient, DB blocks not reloaded
- **Start of each turn** (before streaming): `RemovePeekBlocksAsync` cleans all peek blocks → yields `AutoToolTurnEvent("peek_clean", ...)` (visible auto-tool with stats, dark gray)
- **Tools affected by peek:** `read_file`, `write_file`, `patch_file`, all others

### Peek behavior per tool
| Tool | Peek effect | Always executes? |
|------|-------------|-----------------|
| `read_file` | Block cleaned next turn, result not saved to DB | Yes |
| `write_file` | File block marked peek (cleaned next turn), result skipped | Yes — **file always written** |
| `patch_file` | Diff result not persisted, block cleaned next turn. Args cleaned (oldString/newString removed). | Yes — **patch always applied** |
| `fetch_web` | Standard peek behavior (block cleaned next turn) | Yes |

### `read_file.compress`
- `true` — force dedup on any file
- `false` — disable dedup (even for .log)
- `null` (omit) — auto-detect for `.log` files
- Only activates when reading whole file (no `offset`/`limit`)

### `fetch_web.format`
- `"text"` (default) — strips HTML tags, collapses whitespace
- `"markdown"` — currently same as `"text"`

### Tool result & args cleaning per tool
| Tool | `CleanToolArgs` removes | `ToolResult` (non-peek) |
|------|------------------------|------------------------|
| `read_file` | `"content"` (no-op, no such key) | `""` (content in file block) |
| `write_file` | `"content"` | `""` (content in file block) |
| `patch_file` | `"newString"`, `"oldString"` | `output` (diff) |
| Others | — | `output` |

### Live streaming
- Text/reasoning chunks arrive from LLM API → `TextChunkEvent`/`ReasoningChunkEvent` yielded immediately
- `RenderChunk()` writes each chunk via `Console.Write()` in real-time with color transitions
- Mode switch (reasoning→text or text→reasoning) inserts newline
- On tool boundary, accumulated text flushed to block for persistence (no re-render)
- Chunk stream after tool/file result gets blank line via `RenderState` transition check
- Everything persists to DB as full blocks for replay consistency

### Spacing rules
- **One blank line** between any two content blocks (tool call, result, text, reasoning, file)
- Trailing blank lines after tool/file results removed from `RenderBlock` and streaming handlers
- Inter-block spacing provided by next block's `RenderBlock` (via two-if pattern) or `RenderChunk` transition
- End of replay: two blank lines before CLI header via `ReplayBlocksAsync`

### AutoTool display
- `[AutoTool: peek_clean | {"count":N,"peek":true}]` — dark gray, block type `auto_tool`
- `── Cleaned N peek blocks ─────────────────` header with breakdown by type (icons: 🤖 auto_tool, 📄 file, etc.)
- Result respects `ToolMaxLength["peek_clean"]`: `-1` show, `0` hide, `N` truncate
- User sees truncated display; LLM sees full result in messageList

### FailSafeChatClient streaming
- Yields FCC before tool execution → TurnProcessor creates block
- Yields FRC after tool execution → TurnProcessor renders result (peek: skip DB save)
- `toolResults` in `messageList` keeps **full** result for LLM's next iteration
- `display` yielded to caller may be **truncated** (via `ToolMaxLength`)

### Usage tracking
- DeepSeek cache: `Usage.InputTokenDetails.CachedTokenCount` (hit), `InputTokenCount - CachedTokenCount` (miss), `OutputTokenCount`
- K = last turn hit+miss (input), % = last turn cache rate, $ = cumulative, +$ = last turn cost
- Prices in config as `$/M` tokens, `FormatCost` divides by 1,000,000

### Version
`Version.txt`: `0.2.x`, published up to v0.2.74

### Schema
No migrations. Single `CREATE TABLE IF NOT EXISTS` in `Initialize()` with all columns:
- `blocks`: id, agent_id, number, type, created_at, content, tool_name, data, model, tool_result, updated_at, parent_number, is_deleted
- `config`: key, value, scope, agent_id, updated_at
- `sessions`: id, current_model, next_number, created_at, home_path
- `session_usage`: agent_id, cache_hit, cache_miss, output_tokens, created_at
- `agent_launches`: agent_id, path, last_active_at
