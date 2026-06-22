# Glyphite

**AI agent with tools — right in your terminal.**

Glyphite is a .NET console-based AI agent that runs commands, works with files, searches for information, manages todos, and interacts with external services — all from the command line. Built for agentic workflows with block-based memory, cascading context, and MCP support.

## Features

- **Conversational AI interface** — chat with AI (DeepSeek / OpenAI) directly in the terminal
- **Agent-oriented architecture** — named agents instead of GUID sessions. Each agent has its own history, home directory, and config
- **Built-in tools:**
  - `execute_bash` — shell commands
  - `read_file` / `write_file` / `patch_file` — file operations with diff highlighting
  - `fetch_web` — HTTP requests
  - `search_glob` / `search_grep` — file and content search
  - `todo_write` / `todo_update` — task management within the conversation
  - `memory` — context and memory management (stats, delete, recover blocks)
  - `subagent_run` / `subagent_use` / `subagent_list` — delegate tasks to worker agents
- **MCP protocol** — Model Context Protocol support (`stdio` / `streamablehttp` / `sse`). Every agent (main + subagents) can have its own MCP servers via `Glyphite.{agentName}.json`
- **Block-based memory** — full conversation history stored in SQLite with smart deduplication and compression
  - **`ParentNumber` + cascade** — blocks carry parent references; `memory delete` and `recover` cascade through `Data["parentNumber"]` chains
  - **Todo chain** — only one active list exists; each `todo_update` snapshots the previous one, forming a forward chain you can clip at any point
  - **Indexed queries** — fast context loading via indexed `(agent_id, is_deleted)`
- **Config hot-reload per turn** — changes to `Glyphite.json` / `Glyphite.{agent}.json` are picked up on the next user turn. No restart needed. Every section (Bash, Search, ToolStreaming, McpServers, etc.) refreshes automatically. MCP servers reconnect on config change via hash comparison.
- **ToolMaxLength** — per-tool output length control. Set `0` to hide, `-1` for full output, or `N` for first N characters. Works for all tools including MCP.
- **Content deduplication** — repeated lines compressed in bash, read, and search tool outputs
- **Rich rendering** — syntax highlighting, diffs, color schemes. Color-coded tool rendering for subagent and memory actions.
- **Incremental saving** — conversation blocks are saved as they're generated
- **Live streaming** — text/reasoning chunks rendered in real-time with color transitions and mode switches
- **Peek tool calls** — LLM can mark tool calls as `peek=true` to see the result once before it's truncated to `(peek)`. File writes/patches always execute regardless of peek.
- **Memory clean from messageList** — `memory clean` removes blocks from both SQLite and the in-memory message list, preventing stale data from reaching the LLM.
- **Prompt prefix** — colored segments: DarkGray default, DarkYellow (good cache rate), White (bad rate / significant cost)
- **Auto-tool events** — peek cleanup notifications shown as compact auto-tool blocks
- **Tab completion** — `/*` commands with tab completion
- **Inline args** — `/new MyAgent`, `/use OtherAgent`, `/delete OldAgent`
- **Versioning** — auto-increment patch on every debug build, rollover at >99, version shown in greeting and via `-v`

## Quick start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- DeepSeek or OpenAI API key

### Installation & setup

```bash
git clone https://github.com/Gabatawr/Glyphite.git
cd Glyphite

# Configure your API key via Glyphite.json or environment variable:
# export DeepSeek__ApiKey="sk-..."

# Run (development mode)
dotnet run --project src/Glyphite.Cli
```

### Single-file publish (Linux / WSL)

```bash
# Build and publish with auto-backup of the previous version
./publish.sh

# Add an alias to ~/.bashrc:
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
alias glyphite='~/.glyphite/glyphite'

# Run from any directory
glyphite
```

On first launch, Glyphite will ask for an agent name. On subsequent launches, it resumes the last active agent for the current directory.

Configuration is loaded in cascading order: `appsettings.json` (embedded) → `Glyphite.json` (global) → `Glyphite.{agentName}.json` (agent-specific). All keys can also be set via environment variables (e.g. `DeepSeek__ApiKey`).

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
| `execute_bash` | Execute shell commands with timeout and output limits |
| `read_file` | Read file contents with line numbers, offset/limit for partial reads, and auto-dedup for logs |
| `write_file` | Create / overwrite a file |
| `patch_file` | Partially modify a file (with diff highlighting) |
| `search_glob` | Find files by glob pattern |
| `search_grep` | Search text inside files (with content dedup) |
| `fetch_web` | HTTP request (GET/POST) with text extraction |
| `todo_write` | Create a structured todo list (only one active list at a time) |
| `todo_update` | Update the latest todo list (status, priority, title, add/remove items) — always targets the most recently created list; `title` renames the latest list, does not search; creates a snapshot chain |
| `memory` | Memory management: `stats` (type breakdown), `clean` (soft-delete with optional `cascade`; also removes from messageList), `recover` (restore with optional `cascade`), `list` (view blocks) |
| `subagent_run` | One-shot task execution. Without a name — auto-GUID temp agent created then deleted. With a name + agent exists — dry-run (blocks cleaned after). With a name + no agent — temp agent with config created then deleted. Supports `mode="parallel"` |
| `subagent_use` | Execute a task on a named subagent (auto-creates if not found). Memory and context **accumulate** across calls — the agent persists. `memory` tool is available. Supports `mode="parallel"` |
| `subagent_list` | List all existing agents (excluding current session) with home, model, block count, cache stats |

Any MCP-connected server's tools also become available automatically.

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

The subagent has access to the `memory` tool (`stats`/`clean`/`recover`/`list`), allowing it to manage its own context — clean old blocks, inspect memory, etc. Subagents **do not** have access to `subagent_*` tools (prevents recursive agent creation).

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

### Config loading per agent

When a subagent is created (via `subagent_run` or `subagent_use`), its configuration is loaded from:
1. `Glyphite.json` in the parent agent's working directory
2. `Glyphite.{agentName}.json` in the parent agent's working directory
3. `Glyphite.{agentName}.json` in the subagent's own home directory (if different)

Config is loaded by `SubAgentConfigLoader` every turn — changes to config files are reflected immediately.

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

**Hot-reload:** When MCP server config changes, `McpService` detects the hash change and reconnects automatically on the next turn. No restart needed.

## Config hot-reload

All configuration is reloaded from disk every turn — no `/reload` command needed:

| Section | Applied to | Refresh mechanism |
|---------|-----------|-------------------|
| `DeepSeek.*` | Model, context window | per-turn via `TurnProcessor` |
| `Agent.*` | Max tool iterations, peek settings | per-turn via `TurnProcessor` |
| `ToolStreaming.ToolMaxLength` | Per-tool output display | per-turn via `ConsoleRenderer.RefreshAsync` |
| `McpServers.*` | MCP server connections | hash-based reconnect via `McpService` |
| `Bash.*` | Shell timeouts, forbidden commands | per-call via `BashTool` + per-session via `BashSessionManager` |
| `Search.*` | Search exclusions | per-call via `SearchTools` |
| `Todo.*` | Todo valid statuses | per-call via `TodoTool` |
| `WebFetch.*` | HTTP timeouts | per-call via `WebFetchTool` |
| `Memory.*` | Protected block types, reload agent file | per-turn via `BlockMemoryProvider` |
| `Compression.*` | Compression thresholds | per-turn via `BlockMemoryProvider` |

Changes are reflected immediately on the next user turn — no restart required.

## ToolMaxLength

Control how much of a tool's output is shown in the console. Configured in `Glyphite.json`:

```json
"ToolMaxLength": {
  "execute_bash": -1,     // -1 = full output
  "read_file": 0,         // 0 = hidden (LLM still sees full result)
  "fetch_web": 500,       // N = first N characters
  "codegraph_explore": 0  // works for MCP tools too
}
```

- **`-1`** (default) — full output
- **`0`** — hidden from console (LLM still sees everything)
- **`N > 0`** — first N characters

Works for all tools including MCP. Changes are picked up per-turn without restart.

## Peek tool calls

The LLM can pass `"peek": true` to any tool to mark the result as transient:

- The tool **always executes** (file writes/patches still apply)
- The LLM sees the full result **exactly once** — on the next iteration after the tool completes
- After the LLM generates a response, the result is **truncated to `(peek)`** in the message list
- In the database, the block's `tool_result` is never saved (skipped by `TurnProcessor`)
- Reasoning blocks with `peek=true` are cleaned at the start of the next turn via `RemovePeekBlocksAsync`

**How it works:** `FailSafeChatClient` tracks `_pendingPeekCallIds` during tool execution. After the LLM consumes the results (reads them and generates a response), it replaces the real data with `(peek)` in `messageList`. The LLM sees the data once, then sees only `(peek)` on subsequent iterations.

> Peek is for inspection — use it to read files, check command output, or fetch web pages without cluttering the conversation history.

## Memory clean

The `memory` tool with `clean` action removes blocks from the conversation history:

```
memory clean blocks=[5, 7, 9]
memory clean blocks=[11] cascade=false
```

- **Removes from SQLite** — blocks are soft-deleted (`is_deleted = 1`) and won't appear in future context loads
- **Removes from messageList** — the corresponding `[Block: N, ...]` messages are removed from the in-memory list **after the LLM sees the deletion result once**
- **Cascade** (default `true`) — follows `Data["parentNumber"]` chains to remove parent/child blocks recursively
- **Protected types** — `agent_data`, `user_message`, `agent_message` cannot be deleted

Use `memory recover blocks=[5, 7]` to restore soft-deleted blocks.

## Models

Supported:
- **DeepSeek** — v4-flash, v4-pro (via DeepSeek API with cache metrics)
- **OpenAI** — via Microsoft.Extensions.AI
- Any models compatible with Microsoft.Extensions.AI

## Versioning

The version is stored in `version.txt`. On `dotnet build` in Debug mode, the patch version is auto-incremented. On `dotnet publish -c Release`, the version stays unchanged (the `publish.sh` script bumps it manually).

```bash
glyphite -v       # → 0.6.6
/version          # → Glyphite v0.6.6
```

The greeting shows the version and agent name:
```
Glyphite CLI v0.6.6 — MainAgent 🏠
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
cp ~/.glyphite/backup/glyphite.v0.6.5 ~/.glyphite/glyphite
```

## Testing

Tests directory (`tests/Glyphite.Tests.Unit/`) is referenced in the solution but not yet created — planned for future iterations.

## License

MIT (c) 2026 Gabatawr
