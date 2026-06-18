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

### Recent fixes
- **BashSession marker isolation** — `echo ""` between command and `EXEC_END_xxx` marker prevents file content (without trailing `\n`) from concatenating with marker → fixes `cat`/`head` timeout hang
- **`ReadStdoutAsync` byte reader** — replaced `BeginOutputReadLine()` (1024-char `StreamReader` buffer) with 65536-byte chunks + manual line splitting + `Encoding.UTF8.GetDecoder()` for correct multi-byte decode
- **FileWriteTool/FilePatchTool** — `FileStream` + `UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` (no BOM) + `Flush(true)` (fsync). Fixes WSL `cat`/`head` hang on files without trailing `\n`
- **`fetch_web` per-call HttpClient** — fresh `HttpClient` per call (no singleton `Timeout`/`UserAgent` mutation); removed `HttpClient` DI singleton
- **BashTool subshell workdir** — `(cd "workdir" && command)` prevents persistent directory change
- **ToolMaxLength truncation moved to UI layer** — `FailSafeChatClient` no longer truncates displayed result; file blocks in DB contain full content
- **`peek_clean` made persistent** — `Data["peek"] = true` removed → blocks survive `RemovePeekBlocksAsync`; preserves DeepSeek cache across turns

### Prompt prefix coloring
- `_promptSegments` list with per-segment `ConsoleColor`
- `WriteColoredPrompt()` method renders each segment with its color
- DarkGray = default/neutral
- DarkYellow = K when cache rate ≥ `CacheHitRateThreshold`
- White = % when cache rate < `CacheHitRateThreshold`, +$ when cost ≥ `CostSignificantThreshold`

### Usage tracking
- DeepSeek cache: `Usage.InputTokenDetails.CachedTokenCount` (hit), `InputTokenCount - CachedTokenCount` (miss), `OutputTokenCount`
- K = last request (non-tool-call) hit+miss tokens
- % = total turn aggregate cache rate (all iterations), not last-request-only
- $ = cumulative cost (all turns)
- +$ = total turn cost (sum of all iterations)
- Prices in config as `$/M` tokens, `FormatCost` divides by 1,000,000
- `CostSignificantThreshold` (`CompressionOptions`, default 0.01) — +$ turns white when cost ≥ threshold

### Architecture
- **Abstractions** — interfaces, models, no deps (except `Microsoft.Extensions.AI`)
- **Host** — service implementations (TurnProcessor, FailSafeChatClient, MemoryStore, BlockMemoryProvider), tools, MCP, DI wiring
- **Cli** — UI only (ChatRepl + 3 partials, ConsoleRenderer). No persistence logic.

### Config flow
`appsettings.json` (embedded) + `Glyphite.json` (cwd) + `Glyphite.{agent}.json` (cwd, per-agent overrides)
- `InitializeAsync()` seeds into SQLite DB, `IConfigService.GetOptionsAsync<T>(section)` reads fresh per-tool-call.

### Peek flow
- LLM calls tool with `"peek": true` → tool call block created with `Data["peek"] = true`
- File blocks (read_file/write_file) also get `Data["peek"] = true` if peek
- Tool result NOT saved to block (`UpdateBlockToolResultAsync` skipped for isPeek)
- Within-turn iterations (tool→LLM→tool): LLM sees full result via `messageList` in FailSafeChatClient, DB blocks not reloaded
- **Start of each turn** (before streaming): `RemovePeekBlocksAsync` cleans all peek blocks → yields `AutoToolTurnEvent("peek_clean", ...)` (visible auto-tool with stats, dark gray)
- **Tools affected by peek:** `read_file`, `write_file`, `patch_file`, `fetch_web`, all others

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

### Content dedup (`ContentDedup.cs`)
- Frequency-based line dedup: aliases (ID→line mappings) replace repeated lines
- Configurable: `MinLines` (3), `FrequencyThreshold` (0.05), `MinLineLength` (32), `MaxAliases` (10)
- Auto-dedup for `.log` extensions via `AutoDedupExtensions`

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
- On tool boundary (FCC/FRC), accumulated text flushed to block for persistence (no re-render)
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
- Tracks `ExecutedCallIds` for dedup across iterations
- Accumulates `TotalCacheHitTokens`, `TotalCacheMissTokens`, `TotalOutputTokens` across all iterations

### MCP Service (`McpService`)
- Model Context Protocol: supports `stdio` and HTTP (`streamablehttp`/`sse`) transports
- Async lazy init via `EnsureInitializedAsync`, thread-safe with `SemaphoreSlim`
- Lazy tool discovery via `GetToolsAsync()` — calls `client.ListToolsAsync()`
- `ReconnectAllAsync` / `ReconnectAsync` for server management
- `McpServerInfo` record: Name, Type, Status (Disabled/Connecting/Connected/Failed), Error, ToolCount
- Config via `McpServersConfig.Servers` dict in `appsettings.json` (default: empty)
- Registered via `AddMcp()` extension in DI, tools merged in `ToolRegistry`

### Tool Registry (`ToolRegistry`)
- Discovers built-in tools from `ToolFactory` (BashTool, FileReadTool, FileWriteTool, FilePatchTool, SearchTools, WebFetchTool, MemoryTool, TodoTool)
- Adds MCP tools via `McpService.GetToolsAsync()` as `McpTool` wrappers
- `GetBuiltinTools(sessionId)` returns all registered `AIFunction`s

### `/stats` command features
- Shows block type distribution (icons: 👤 user, 💬 agent, 🧠 reasoning, 🔧 tool, 🤖 auto_tool, 📄 file, 📋 todo)
- Total blocks, token count with percentage of context window
- Limits display (threshold at `AutoThreshold`% of context window)
- Hides `system_info` and `agent_data` from display

### Memory Tool
- `memory stats` — shows block type distribution and total tokens (used by LLM)
- `memory recover [block_numbers]` — restores soft-deleted blocks
- `memory delete` — via `delete` action on block numbers

### Version
`Version.txt`: `0.3.x`, published up to v0.3.37

### Schema
No migrations. Single `CREATE TABLE IF NOT EXISTS` in `Initialize()` with all columns:
- `blocks`: id, agent_id, number, type, created_at, content, tool_name, data, model, tool_result, updated_at, parent_number, is_deleted
- `config`: key, value, scope, agent_id, updated_at
- `sessions`: id, current_model, next_number, created_at, home_path
- `session_usage`: agent_id, cache_hit, cache_miss, output_tokens, created_at
- `agent_launches`: agent_id, path, last_active_at

### Known issues
- Tests dir `tests/Glyphite.Tests.Unit` referenced in `.slnx` but does **not exist** on disk
- `PeekToolReasoning` (`AgentOptions`) controls whether reasoning blocks are marked peek during tool iterations
- `/clone` is the actual command name (not `/fork`) — used in first-run selection, main command handler, and help hint
- `appsettings.Development.json` contains the live DeepSeek API key (gitignored, copied to output)
