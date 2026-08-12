<table>
  <tr>
    <td width="200" valign="middle">
      <img src="Glyphite.png" alt="Glyphite logo" width="200" height="200">
    </td>
    <td valign="middle">
      <h1>Glyphite</h1>
      <p><strong>AI agent with tools — right in your terminal.</strong></p>
      <p>Glyphite is a .NET console-based AI agent that runs commands, works with files, searches for information, manages todos, and interacts with external services — all from the command line. Built for agentic workflows with block-based memory, cascading context, MCP support, and subagent delegation.</p>
    </td>
  </tr>
</table>

## Features

- **Conversational AI interface** — chat with AI (any LLM provider) directly in the terminal
- **Agent-oriented architecture** — named agents instead of GUID sessions. Each agent has its own history, home directory, and config
- **SessionManager** centralizes agent lifecycle — create, clone, switch, delete agents; persist/resume sessions; hot-reload config per agent
- **InputHistory** shared between sessions — user messages and commands accessible across agent switches
- **Built-in tools:**
  - `bash` — shell commands with interactive confirmation for dangerous commands
  - `read_file` / `write_file` / `patch_file` — file operations with diff highlighting
  - `fetch_web` — HTTP requests
  - `search_glob` / `search_grep` — file and content search
  - `todo` — task management with create/update/list, title-based multi-list support
  - `kvstore` — key-value store with vault/config scopes, glob masks, TTL, dry-run confirm flow
  - `pocket_list` / `pocket_add` / `pocket_set` / `pocket_remove` / `pocket_run` — user-defined tool aliases (bash templates + typed arg schemas); favorites materialize as native tools (`<name>_pocket`); local/global scope with shadows
  - `memory` — memory statistics (stats)
  - `subagent_run` / `subagent_use` / `subagent_list` — delegate tasks to worker agents
- **MCP protocol** — Model Context Protocol support (`stdio` / `streamablehttp` / `sse`). Every agent (main + subagents) can have its own MCP servers via `Glyphite.{agentName}.json`. Tools are prefixed with `{serverName}_` (e.g. `codegraph_explore`). Per-server and per-tool execution settings via `McpExecution` config.
- **Block-based memory** — full conversation history stored in SQLite with smart deduplication and compression
  - **Todo chain** — only one active list exists; each `todo_update` snapshots the previous one, forming a forward chain you can clip at any point
  - **Indexed queries** — fast context loading via indexed `(agent_id, is_deleted)`
- **Atomic auto-compaction** — two strategies (configurable via `Strategies` dict with flags):
  - **`fibo`** — Fibonacci zones (1, 1, 2, 3, 5, 8...), zone 3+ fully sent to LLM & summarized **in parallel**, zones 1-2 intact
  - **`struct`** — full history (unfiltered) → one structured LLM summary (Goal/Progress/Decisions/Files/Next Steps); summary placed **after** preserved zones
  - If summarization fails, blocks fall back intact
  - **No UI freeze:** `[AutoTool: compression]` notification appears immediately (via `EvaluateCompactionStatusAsync`), then slow summarization runs in background
- **Reasoning auto-compaction** — large `agent_reasoning` blocks (>6K chars) are automatically compressed by the LLM after each turn. Configurable via `Compression.AutoCompressReasoning` and `AutoCompressReasoningMaxSize`.
- **Interactive confirmation for dangerous commands** — when `bash` is called with a command matching `CheckRequireCommands`, an interactive panel appears: `[OK] [Stop] [Check]`. Arrow keys navigate, Enter confirms, timer auto-selects. Last choice persists per-agent via KVStore. Subagents skip the panel and go directly to SafetyChecker.
- **SafetyChecker** — LLM-powered safety evaluation for shell commands in subagents (and the `Check` option). Reads conversation context, makes a direct LLM call, and returns `allow:true/false` with a reason. Usage is recorded to session stats. Falls back to `allow` on errors.
- **Config hot-reload per turn** — changes to `Glyphite.json` / `Glyphite.{agent}.json` are picked up on the next user turn. No restart needed. Every section (Bash, Search, ToolStreaming, McpServers, etc.) refreshes automatically. MCP servers reconnect on config change via hash comparison.
- **ToolStreaming** — per-tool output and display control. Set `MaxSize` (`-1` full, `0` hidden, `N` trim), configure `HiddenArgs` (arguments hidden from console), and per-tool `Peek`/`Timeout`/`MaxSize` via `ToolExecution`. Works for all tools including MCP. Wildcard `*` support (e.g. `codegraph_*` matches all tools from that server).
- **ToolExecution** — per-tool execution settings (timeout, maxSize, peek) configured as an array. Serverside truncation with 1/3+2/3 view and full output saved to a temp file. Works for all tools including MCP.
- **Content deduplication** — repeated lines compressed in bash, read, and search tool outputs
- **Rich rendering** — syntax highlighting, diffs, color schemes. Color-coded tool rendering for subagent and memory actions.
- **Markdown table formatting** — agent-generated markdown tables are automatically detected and rendered as formatted console tables with proportional column widths, centered headers, multi-line cell support, and word-wrap. Works in streaming, replay, and subagent results.
- **Incremental saving** — conversation blocks are saved as they're generated
- **Live streaming** — text/reasoning chunks rendered in real-time with color transitions and mode switches
- **Peek tool calls** — LLM can mark tool calls as `peek=true` to see the result once before it's truncated to `(peek)`. File writes/patches always execute regardless of peek.
- **Auto-tool events** — compaction, reasoning compression, and safety-check notifications shown as compact auto-tool blocks
- **Structured file logging** — all host service logs written to `~/.glyphite/logs/{date}-{run}.log` via Serilog + `ILogger<T>`. No console noise from subagents.
- **Prompt prefix** — colored segments: DarkGray default, DarkYellow (good cache rate), White (bad rate / significant cost), Magenta (context large but all zones already compressed)
- **Tab completion** — `/*` commands with tab completion
- **Inline args** — `/new MyAgent`, `/use OtherAgent`, `/delete OldAgent`
- **Versioning** — auto-increment patch on every debug build, rollover at >99, version shown in greeting and via `-v`

## Quick start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- API key for your LLM provider (tested with DeepSeek, should work with any OpenAI-compatible API)

### Installation & setup

```bash
git clone https://github.com/Gabatawr/Glyphite.git
cd Glyphite

# Configure your API key via Glyphite.json under the LLM section
# (see appsettings.json for the default structure)

# Run (development mode)
dotnet run --project src/Glyphite.Cli
```

### Single-file publish (Linux / WSL)

```bash
# Build and publish with auto-backup of the previous version
./publish.sh

# Add an alias to ~/.bashrc:
alias glyphite='~/.glyphite/glyphite'

# Run from any directory
glyphite
```

The first launch creates a global config template at `~/.glyphite/Glyphite.json` — set `LLM:ApiKey` there once, and `glyphite` works from any directory without per-folder config files. The global file is a **one-time snapshot**: it is never overwritten, so new keys added later to the source `appsettings.json` flow through automatically, but **changed values of existing keys do not** — the global file wins. To pick up a changed default, edit `~/.glyphite/Glyphite.json` manually or delete it (it is recreated from the embedded template on next launch; you will need to re-enter the API key).

First-run flow: when no API key is configured anywhere, Glyphite asks how to proceed — enter the key (saved to the global config), create a local `Glyphite.json` in the current folder (the API key is prompted and written into it right away), or skip. When a global key exists and you launch from a new folder, it asks once whether to use the global config or create a local one (creating a local file also prompts for the key); the choice is remembered in `state.json` next to the global config, so the prompt appears only once per folder. Non-interactive runs (redirected stdin) silently use the global config. A config file in the working directory is created **only** through this prompt — never automatically.

On first launch, Glyphite will ask for an agent name. On subsequent launches, it resumes the last active agent for the current directory.

Configuration is loaded in cascading order: `appsettings.json` (embedded defaults) → `~/.glyphite/Glyphite.json` (global — install location, shared across all working directories) → `Glyphite.json` in the working directory (per-project overrides) → `Glyphite.{agentName}.json` (agent-specific).

## Commands

| Command | Description |
|---------|-------------|
| `/new` | Create a new agent / reset an existing one |
| `/new MyAgent` | Create with inline name |
| `/clone` | Clone an agent's history to a new name (two-step: pick source, enter name) |
| `/use` | Switch to another agent (from the list of existing ones) |
| `/use AgentName` | Switch with inline name |
| `/delete` | Delete an agent permanently (select from list, excludes current session) |
| `/delete AgentName` | Delete with inline name |
| `/stats` | Current agent statistics: blocks by type, input/output tokens, cache rate, cost |
| `/version` | Show Glyphite version |
| `/models` | List available models and switch between them |
| `/exit` | Exit |

## Tools

All tools are available to the AI agent and can be invoked in conversation:

| Tool | Description |
|------|-------------|
| `bash` | Execute shell commands with timeout and output limits. `back=true` runs as background process, returns taskId immediately. Commands matching `CheckRequireCommands` trigger interactive confirmation panel ([OK]/[Stop]/[Check]) with arrow keys and timer |
| `bash_back` | Manage background bash tasks: `list` (show all tasks), `wait` (block until done, kills on timeout), `partial` (poll current output without killing) |
| `read_file` | Read file contents with line numbers, offset/limit for partial reads, and auto-dedup for logs |
| `write_file` | Create / overwrite a file |
| `patch_file` | Partially modify a file (with diff highlighting) |
| `search_glob` | Find files by glob pattern |
| `search_grep` | Search text inside files (with content dedup) |
| `fetch_web` | HTTP request (GET/POST) with text extraction |
| `kvstore` | Key-value store for agent data. Two scopes: `vault` (persistent table) and `config` (agent config + session overrides). `get`/`set` actions with glob masks (`*`/`?`), TTL (vault only), dry-run confirm flow for masked sets. Empty value = delete. Ephemeral (subagent_run) agents use in-memory only — no DB leaks. |
| `todo` | Create, update, or list todo lists — title as immutable ID for multi-list support. `create(title, items)`, `update(title, items)`, `list(title?)` — list all or by title. Statuses: pending, in_progress, done, cancelled, blocked. Update by index or by text (no index = match by text, new text = add item). |
| `memory` | Memory statistics: `stats` (block type distribution, token usage, cache stats, cost) |
| `pocket_list` | List pocket tools — user-defined tool aliases stored in the agent's vault. Compact by default (grouped by category), `details=true` for full cards. Glob filter on name, exact filter on category. Shows local + global entries (`[global]`, `[shadowed]` markers) |
| `pocket_add` | Add a pocket tool: `name`, `desc`, bash `cmd` template with `{arg}` placeholders, optional `args` schema (JSON array with `req`/`def`/`raw`), `cwd`, `category`. Options: `materialize` (native tool on next turn), `scope` (`local`/`global`). Safety preflight at add |
| `pocket_set` | Update one field of an existing pocket tool: `materialize`, `name` (rename), `scope` (move local↔global), `category`, `args`, `cwd`, `desc`, `cmd` (cmd change re-runs the safety preflight) |
| `pocket_remove` | Remove a pocket tool — effective entry by default (removes a local shadow, the global stays); `scope="global"` removes the shared entry |
| `pocket_run` | Execute a pocket tool immediately by name (same-turn, no reload needed) |
| `subagent_run` | One-shot task execution (ephemeral). Without a name — auto-GUID temp agent created then deleted. With a name + agent exists — dry-run (blocks cleaned after). With a name + no agent — temp agent with config created then deleted. Ephemeral: usage restored to pre-run state, no compaction runs, blocks deleted after. Supports `mode="parallel"` |
| `subagent_use` | Execute a task on a named subagent (auto-creates if not found). Memory and context **accumulate** across calls — the agent persists. `memory` tool is available. Supports `mode="parallel"` |
| `subagent_list` | List all existing agents (excluding current session) with home, model, block count, cache stats |

Any MCP-connected server's tools also become available automatically.

## Pocket tools

Pocket tools are user-defined tool aliases stored in the agent's KV vault. Each entry wraps a bash command template with a typed argument schema — a way to give the agent reusable custom tools without code changes.

### Storage

Entries live in `kv_store` under keys `pocket.<name>` (JSON values). Local entries are scoped to the agent's vault; global entries use the shared sentinel namespace (`agent_id = "__global__"`) and are visible to **all** agents.

### Materialization

- Entries with `materialize=true` (favorites) are exposed as **native typed tools** on the next tool load — named `<name>_pocket` so their origin is explicit (`git_st` → `git_st_pocket`).
- The suffix removes collisions with builtins: a pocket entry named `bash` materializes as `bash_pocket`.
- Non-materialized entries are executed on demand via `pocket_run`.
- The toolset is rebuilt every turn, so materialization takes effect automatically — no restart or hot-reload needed.

### Scope: local vs global

| Scope | Stored | Visible to |
|-------|--------|------------|
| `local` (default) | agent's vault | only this agent |
| `global` | shared namespace (`__global__`) | all agents — visible, executable, editable |

- **Shadows** — a local entry with the same name takes precedence over the global one for this agent (effective resolution in `pocket_run`, editing, and materialization). The global stays intact for others.
- **Editing** — `pocket_set` / `pocket_remove` operate on the effective entry by default (local first, else global); pass `scope="global"` to edit the shared entry explicitly, even when shadowed.
- **Materialization is independent of scope** — a global entry with `materialize=false` stays pocket-only; with `materialize=true` it becomes a native tool for **all** agents (no per-agent flags).
- **Moving** — `pocket_set X scope=global` moves a local entry to the shared space (and back); a collision in the target scope blocks the move.

### Safety

Command templates are safety-checked **once at add time**: forbidden-command/directory hard blocks, plus an interactive confirmation prompt when the template matches `CheckRequireCommands`. Approved entries skip the interactive prompt on execution (hard blocks still apply at run time). Changing `cmd` via `pocket_set` re-runs the preflight; a rejected template leaves the entry unchanged.

## Subagent architecture

Subagents enable the main agent to delegate tasks to specialized worker agents.

### `subagent_run` — one-shot temporary execution

Three modes:

| `name` | Agent exists | Behavior |
|--------|-------------|----------|
| not provided (auto-GUID) | — | Creates a temp agent, runs the task, **deletes it entirely** |
| provided | **No** | Creates a temp agent with config, runs the task, **deletes it entirely** |
| provided | **Yes** | **Dry-run**: executes the task, then **cleans only the delta blocks/usage** created during this run. Existing memory is preserved |

### `subagent_use` — persistent named agent

| Condition | Behavior |
|-----------|----------|
| Agent **exists** | Executes the task, **preserves** blocks and usage. Context accumulates across calls |
| Agent **doesn't exist** | **Auto-creates** the agent (with config), executes, preserves context |

The subagent has access to the `memory` tool (`stats`), allowing it to inspect memory statistics. Subagents **do not** have access to `subagent_*` tools (prevents recursive agent creation).

### Escape cancellation

When the user presses Escape during a subagent task:

1. CancellationToken propagates through the **entire chain**: tool lambda → `RunAgentTask` → `TurnProcessor.ProcessAsync`
2. The subagent's `ProcessAsync` throws `OperationCanceledException`
3. **Dry-clean runs**: for `subagent_run` on a named agent, blocks created during this run are deleted and usage is cleared
4. **`pending_runs` record is cleared** — no orphan agents left in DB
5. **Scope is disposed** — `SubAgentManager` removes the entry, semaphore released
6. For GUID temp agents — `DeleteSessionAsync` runs in `finally`, agent entirely removed
7. **Usage from completed iterations is already saved** (per-iteration write via `OnIterationRecorded`)

### Crash safety (`pending_runs` table)

If the process crashes during a subagent task, a record in `pending_runs` table persists in SQLite:

| Record mode | Recovery action |
|-------------|----------------|
| `"run"` (GUID agent) | `CleanupOrphanRunsAsync` deletes the orphan agent entirely |
| `"run-dry"` (named agent) | Clears usage + deletes blocks since checkpoint — agent returns to pre-run state |
| `"use"` (persistent) | Nothing to clean — usage saved per-iteration, agent stays intact |

Cleanup runs automatically at the start of the **next** `subagent_run`/`subagent_use` call.

### Parallel execution

Both `subagent_run` and `subagent_use` support **parallel execution** via `mode="parallel"`:

```
subagent_use(name="searcher", task="find files", mode="parallel")
subagent_use(name="writer", task="write report", mode="parallel")
```

- Tools with `mode="parallel"` are grouped into a batch and executed concurrently via `Task.WhenAll`
- Tools with `mode="sequential"` (default) or without `mode` are executed one at a time
- If two parallel calls use the **same agent name**, they're automatically split into sequential groups to prevent race conditions
- Parallel-safe tools: `read_file`, `fetch_web`, `search_glob`, `search_grep`, `subagent_use`, `subagent_run`

### Config loading

Both CLI agents and subagents use the unified `ISessionConfigLoader` (old `ConfigLoader.cs` removed). Called every turn:

1. **Home → DB** — config files from the agent's home directory are read, compared with existing DB keys (change detection), and only home-originated keys are persisted to DB.
2. **Cascade merge** — `DB session keys` → `parentCwd/Glyphite.json` → `parentCwd/Glyphite.{id}.json` → `agentCwd/Glyphite.json` → `agentCwd/Glyphite.{id}.json` (each overrides the previous).
3. **Auto-migrate** — if the original home directory was deleted, the current working directory is adopted as the new home.
4. **Overlay** — if agent is not at home, the merged config is set as session overlay; otherwise `IConfiguration` + DB suffice.

## Auto-compaction

When enabled, Glyphite automatically compresses old conversation history via LLM summarization. Two strategies are available, toggled via `Strategies` dictionary flags:

```json
{
  "Compression": {
    "AutoCompress": true,
    "AutoThreshold": 20,
    "Strategies": {
      "fibo": true,
      "struct": false
    }
  }
}
```

- **Trigger:** when last request tokens exceed `AutoThreshold`% of the context window (e.g., 20% of 1M = 200K tokens)
- **Strategy selection:** if one strategy is enabled — it's used. If both — randomly picked each turn (visible in `[AutoTool: compression]`)

### `fibo` (Fibonacci zones)
- History is grouped into Fibonacci-sized zones (1, 1, 2, 3, 5, 8... turns) from newest to oldest.
- **Zones 1-2** (1+1 newest turns) preserved intact — **all blocks**, including tool calls, auto_tool results, reasoning.
- **Zones 3+** fully sent to LLM (all block types), each zone summarized **in parallel** with structured template: `## Topics / Key Actions / Results / State Changes / Open & Carried Over`.
- Subagent tools (`subagent_run`/`subagent_use`) preserved in summarization.

### `struct` (structured cut)
- Every block from `agent_data` (exclusive) to the end — **unfiltered** — sent to LLM in **one** call with structured template: `## Goal / Progress / Key Decisions / Relevant Files / Next Steps`.
- LLM sees the full picture (all block types — tool calls, results, reasoning, turn messages), producing a single comprehensive summary that covers **everything**, including the last 2 preserved turns.
- Summary block placed **after** preserved zones (last 2 turns). Order in DB: `agent_data → preserved turns → struct summary`.
- All old blocks (except `agent_data`) replaced atomically by the new summary.

### Common
- **Fail-safe:** if summarization fails, old blocks kept intact (no data loss)
- **Atomic replacement:** summaries + preserved blocks inserted atomically via `ReplaceBlocksSinceAsync` in a single SQLite transaction. On crash — rollback, nothing lost.
- **Usage tracking:** compaction LLM calls record hit/miss/output tokens to session stats
- **Notification:** `[AutoTool: compression | {"AutoCompress":true,"Strategy":"fibo",...}]` shown before the LLM call

## MCP (Model Context Protocol)

Glyphite supports MCP servers via `stdio`, `streamablehttp`, and `sse` transports. Configure servers in `Glyphite.json`:

```json
{
  "Glyphite": {
    "McpServers": {
      "Servers": {
        "my-server": {
          "Enabled": true,
          "Type": "stdio",
          "Command": "npx",
          "Args": ["-y", "@org/mcp-server"],
          "TimeoutSeconds": 30
        }
      }
    }
  }
}
```

Per-agent MCP servers via `Glyphite.{agentName}.json` — loaded only when that agent is active.

**Tool name prefixing:** MCP tools are prefixed with `{serverName}_` to avoid name collisions. For example, a tool `explore` from server `codegraph` becomes `codegraph_explore`. Use the prefixed name in `ToolStreaming`, `ToolExecution`, and `McpExecution` configuration. The prefix is stripped before the call reaches the MCP server — the server always sees the original tool name.

### McpExecution — per-server and per-tool settings

Control execution parameters for MCP tools. Configured in `Glyphite.json` under `McpServers.McpExecution`:

```json
"McpExecution": [
  { "Mcp": "*",          "Options": { "Timeout": 300, "MaxSize": 100000, "Peek": false }},
  { "Mcp": "codegraph",  "Tool": "*",  "Options": { "Peek": true }},
  { "Mcp": "codegraph",  "Tool": "search", "Options": { "Timeout": 60 }}
]
```

Resolution priority (highest wins, non-null fields merge):
1. Exact `Mcp` + exact `Tool` (highest)
2. `Mcp="*"` + exact `Tool`
3. Exact `Mcp` + `Tool="*"`
4. `Mcp="*"` + `Tool="*"`
5. Exact `Mcp` (server-level)
6. `Mcp="*"` (server-level wildcard, lowest)

**Hot-reload:** When MCP server config changes, `McpService` detects the hash change and reconnects automatically on the next turn. No restart needed.

## Config hot-reload

All configuration is reloaded from disk every turn — no `/reload` command needed:

| Section | Applied to | Refresh mechanism |
|---------|-----------|-------------------|
| `LLM.*` | Model, context window, API key, endpoint | per-turn via `TurnProcessor` |
| `Agent.*` | Max tool iterations | per-turn via `TurnProcessor` |
| `ToolStreaming.*` | Per-tool display (MaxSize, HiddenArgs) | per-turn via `ConsoleRenderer.RefreshAsync` |
| `ToolExecution.*` | Per-tool peek/timeout/maxSize defaults | per-turn via `ToolRegistry.WrapWithConfig` |
| `McpServers.*` | MCP server connections | hash-based reconnect via `McpService` |
| `McpExecution.*` | Per-server/per-tool MCP execution settings | per-turn via `McpService.GetToolsAsync` |
| `Bash.*` | Shell timeouts, forbidden commands, CheckRequireCommands | per-call via `BashTool` + per-session via `BashSessionManager` |
| `Search.*` | Search exclusions | per-call via `SearchTools` |
| `Todo.*` | Todo valid statuses | per-call via `TodoTool` |
| `WebFetch.*` | HTTP settings | per-call via `WebFetchTool` |
| `Memory.*` | Reload agent file | per-turn via `BlockMemoryProvider` |
| `Compression.*` | Compression thresholds, AutoCompressReasoning | per-turn via `TurnProcessor` |

Changes are reflected immediately on the next user turn — no restart required.

## ToolStreaming

Control how tools are displayed and truncated in the console. Configured in `Glyphite.json` as an array:

```json
"ToolStreaming": [
  { "Tool": "bash",       "Options": { "MaxSize": -1, "HiddenArgs": [] }},
  { "Tool": "read_file",  "Options": { "MaxSize": 0,  "HiddenArgs": [] }},
  { "Tool": "write_file", "Options": { "MaxSize": 0,  "HiddenArgs": ["content"] }},
  { "Tool": "patch_file", "Options": { "MaxSize": 0,  "HiddenArgs": ["old", "new"] }}
]
```

- **`MaxSize`**: `-1` full output (default), `0` hidden from console (LLM still sees everything), `N > 0` first N characters
- **`HiddenArgs`**: list of argument names to hide from console output (e.g. file content)

Works for all tools including MCP (use prefixed name like `codegraph_explore`). Wildcard `*` matches any tool name suffix (e.g. `codegraph_*` matches all tools from the `codegraph` server). Changes are picked up per-turn without restart.

> **Note:** the `ToolStreaming` and `ToolExecution` configs control **independent** MaxSize values. `ToolStreaming.MaxSize` controls what's shown in the console; `ToolExecution.MaxSize` controls what's sent to the LLM server-side (via `ToolConfigDecorator`). You can hide output from the console while still showing full output to the LLM, or vice versa.

## ToolExecution

Per-tool execution settings — peek, timeout, and output size. Configured in `Glyphite.json` as an array:

```json
"ToolExecution": [
  { "Tool": "bash",       "Options": { "Timeout": 300, "MaxSize": 100000, "Peek": false }},
  { "Tool": "read_file",  "Options": { "Timeout": 30,  "MaxSize": 100000, "Peek": false }},
  { "Tool": "write_file", "Options": { "Timeout": 30,  "MaxSize": 100000, "Peek": true }},
  { "Tool": "search_glob","Options": { "Timeout": 30,  "MaxSize": 100000, "Peek": true }},
  { "Tool": "codegraph_*", "Options": { "Timeout": 60, "Peek": true }}
]
```

- **`Peek`** — whether the tool result is marked as peek by default (LLM can still override via `extra_cfg.peek`)
- **`Timeout`** — max execution time in seconds before the tool is cancelled. Applied via `CancellationTokenSource` linked to the caller's token
- **`MaxSize`** — max output characters. When exceeded:
  - Full output is saved to a temp file: `{tmpDir}/{agentId}/{toolName}_{timestamp}.out`
  - Truncated view shows **1/3 from top + truncation notice + 2/3 from bottom** (so the LLM sees both the beginning and the end)
  - If no `tmpDir` is configured (subagents), simple inlined truncation with a length note

The LLM can override any setting per-call via `extra_cfg` (e.g. `"extra_cfg": { "peek": true, "timeout": 600 }`).

Works for all tools including MCP — use the prefixed tool name (e.g. `codegraph_explore`) or wildcard suffix (e.g. `codegraph_*`).

## Interactive confirmation for dangerous commands

When the model calls `bash` with a command matching `CheckRequireCommands`, an interactive panel appears in the console:

```
· 5s ⚠ [OK]  Stop  Check · rm -rf /tmp/test
```

| Key | Action |
|-----|--------|
| `←` `→` | Cycle highlight, **disable timer** |
| `Enter` | Confirm highlighted choice |
| Timer expires | Auto-selects highlighted choice |

Three choices:

| Choice | Behavior |
|--------|----------|
| **OK** | Command executes normally |
| **Stop** | Command blocked, model receives `"Command blocked by user"` |
| **Check** | LLM-powered safety evaluation via `SafetyChecker` |

### Persistence

The last selected choice is saved per-agent in KVStore under `confirmation_last_choice`. On the next confirmation prompt, the saved choice is pre-selected.

### Subagent behavior

Subagents (`subagent_run` / `subagent_use`) **skip the panel** entirely — the `isSubAgent` flag causes an immediate return of `Choice.Check`. The command goes directly to `SafetyChecker` for LLM evaluation.

### Configuration

```json
"Bash": {
  "CheckRequireCommands": [
    "rm", "dd", "chmod", "chown",
    "systemctl", "apt install", "pip install",
    "iptables", "docker system prune", ...
  ],
  "CheckRequireCommandsTimeout": 5
}
```

`CheckRequireCommandsTimeout` — seconds before timer auto-selects the highlighted choice (default 10, set to 5 in defaults).

## SafetyChecker

LLM-powered safety evaluation for shell commands. Used when the user selects **Check** in the interactive confirmation panel, or automatically for subagents.

```
Command → InteractiveConfirmation.ShowAsync
  → isSubAgent? → Choice.Check (skip panel)
  → User pressed Check? → SafetyChecker.CheckAsync
  → Reads last N conversation blocks for context
  → Makes a direct LLM call (same model, preserves cache)
  → Returns {allow: true/false, why: "..."}
  → allow → command executes
  → block → model receives error with reason
```

- **Context-aware:** SafetyChecker reads blocks from the last `turn` marker (or `user_message` / `agent_task` fallback) to the end — full current turn context
- **Usage recorded:** hit/miss/output tokens are saved to session stats via `UsageParser.Normalize`
- **Fallback to safe:** on LLM failure or malformed JSON, defaults to `allow: true` with a note
- **No blocking on errors:** if the LLM call fails, the command proceeds

## Peek tool calls

The LLM can pass `"peek": true` to any tool to mark the result as transient. Per-tool peek defaults can be configured via `ToolExecution`:

```json
"ToolExecution": [
  { "Tool": "write_file",   "Options": { "Peek": true }},
  { "Tool": "search_glob",  "Options": { "Peek": true }},
  { "Tool": "bash",         "Options": { "Peek": false }}
]
```

The LLM can override the default via `"extra_cfg": { "peek": true }` for any tool.

When peek is active:
- The tool **always executes** (file writes/patches still apply)
- The LLM sees the full result **exactly once** — on the next iteration after the tool completes
- After the LLM generates a response, the result is **truncated to `(peek)`** in the message list
- In the database, the block's `tool_result` is never saved (skipped by `TurnProcessor`)
- Reasoning blocks are **not** peek-tagged anymore — they are auto-compacted via `Compression.AutoCompressReasoning` (see below)

**How it works:** `FailSafeChatClient` tracks `_pendingPeekCallIds` during tool execution. After the LLM consumes the results (reads them and generates a response), it replaces the real data with `(peek)` in `messageList`. The LLM sees the data once, then sees only `(peek)` on subsequent iterations.

> Peek is for inspection — use it to read files, check command output, or fetch web pages without cluttering the conversation history.

## Reasoning auto-compaction

Large `agent_reasoning` blocks are automatically compressed after each turn. This replaces the old "peek reasoning" system.

```json
"Compression": {
  "AutoCompressReasoning": true,
  "AutoCompressReasoningMaxSize": 6000
}
```

- **Trigger:** after each turn, all `agent_reasoning` blocks > `AutoCompressReasoningMaxSize` chars are compacted
- **Process:** blocks are compacted **in parallel** by the LLM, each reduced to ~`maxSize/4/2` tokens (~750 chars)
- **Result:** a new summary `agent_reasoning` block replaces the original; `AutoTool: compress_reasoning` is shown inline
- **Usage recorded:** compaction LLM calls save hit/miss/output to session stats
- **Parallel:** all oversized reasoning blocks are compacted concurrently via `Task.WhenAll`

## Models

Glyphite uses the OpenAI-compatible API via `Microsoft.Extensions.AI.OpenAI`. Any provider that speaks the OpenAI protocol works — just configure the endpoint, API key, and model in `Glyphite.json`.

**Tested with:**
- **DeepSeek** — v4-flash, v4-pro (cache metrics via `Usage.InputTokenDetails.CachedTokenCount`)

**Should work (format parsers included):**
- **OpenAI** — cache via `usage.prompt_tokens_details.cached_tokens`
- **Anthropic** — cache via `usage.cache_read_input_tokens`
- **Google Gemini** — cache via `usageMetadata.cachedContentTokenCount` (also offers an OpenAI-compatible endpoint)

## Logging

All host service logs are written to structured files via Serilog:

```
~/.glyphite/logs/22-06-2026-1.log
~/.glyphite/logs/22-06-2026-2.log
```

- **Path:** `~/.glyphite/logs/{dd-MM-yyyy}-{run}.log` (auto-rotated per run)
- **Format:** `2026-06-22 12:34:56.789 +00:00 [INF] Turn start session Agent0605, model deepseek-v4-flash`
- **Level:** Information and above (errors, warnings, info)
- **Scope:** All host services via `ILogger<T>` (TurnProcessor, CompactionService, McpService, FailSafeChatClient, ConfigService, BashSessionManager)
- **Subagent isolation:** subagent logs go to the same file, but never to console — no UI pollution

Key log events:
- Turn start/end with session ID and usage stats (hit/miss/output)
- Compaction start/end with zone and summary counts
- Tool iteration count and accumulated tokens per turn
- MCP connection/disconnection/reconnection events
- Error conditions (parsing failures, process kill errors, etc.)

## Architecture notes & known limitations

- **Single-writer SQLite** — each agent has its own SQLite database with a single write path (serialized via `SemaphoreSlim` in the repositories; WAL mode allows concurrent reads). Only one turn writes at a time; concurrent tool writes are funneled through the same lock. Do not open the DB from a second process while the CLI is running.
- **Per-agent scoped services** — the CLI creates a DI scope per agent session. `IConfigService`, repositories, `ToolRegistry`, `CompactionService`, etc. are scoped to the agent, so config hot-reload and tool state never leak across agents. Subagents get their own scope and DB.
- **Compaction failure fallback** — if the summarization LLM call fails, both strategies (`fibo` and `struct`) preserve the original turn blocks intact instead of dropping them: the compaction becomes a renumbering no-op and no history is lost. This is verified by tests.
- **Tool execution defaults** — per-tool `peek` / `maxSize` / `timeout` defaults are centralized in `ToolExecutionDefaults` (`src/Glyphite.Host/Tools/`); per-tool overrides come from `Glyphite:ToolExecution` (builtin tools) or `Glyphite:McpExecution` (MCP tools). MCP tools get a longer default timeout (300s vs 120s for builtins) because they cross process boundaries.
- **Streaming cancellation is graceful** — Escape cancels the outer stream after the current iteration; the inner LLM stream runs to completion so partial responses are persisted. Usage is written per-iteration, so a crash mid-turn never loses token accounting.

## Versioning

The version is stored in `version.txt`. On `dotnet build` in Debug mode, the patch version is auto-incremented. On `dotnet publish -c Release`, the version stays unchanged (the `publish.sh` script bumps it manually).

```bash
glyphite -v       # → 1.0.0
/version          # → Glyphite v1.0.0
```

The greeting shows the version and agent name:
```
Glyphite CLI v1.0.0 — MainAgent 🏠
```

## Publishing and backups

Use `./publish.sh` to publish:

```bash
./publish.sh
# 1. Archives the previous version → ~/.glyphite/backup/glyphite.v{version}
# 2. Saves the current binary as .prev
# 3. Bumps patch version
# 4. Publishes a new single-file binary (linux-x64, self-contained)
# 5. Copies libe_sqlite3.so for P/Invoke
```

Rollback:
```bash
cp ~/.glyphite/backup/glyphite.v1.0.0 ~/.glyphite/glyphite
```

## Testing

Tests are in the `tests/Glyphite.Tests.Unit/` directory with 184 tests covering the turn pipeline (TurnProcessor, FailSafeChatClient, ToolExecutor), compaction strategies (fibo/struct incl. failure fallback), BashSessionManager, usage parsing (all provider formats), ContentDedup, configuration validation, the data layer (SessionRepository, BlockRepository), and FilePatchTool — written with xUnit + NSubstitute.

## License

MIT (c) 2026 Gabatawr
