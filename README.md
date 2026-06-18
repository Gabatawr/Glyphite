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
- **Block-based memory** — full conversation history stored in SQLite with smart deduplication and compression
  - **`ParentNumber` + cascade** — blocks carry parent references; `memory delete` and `recover` cascade through `Data["parentNumber"]` chains
  - **Todo chain** — each `todo_update` snapshots the previous one, forming a forward chain you can clip at any point
  - **Indexed queries** — fast context loading via indexed `(agent_id, is_deleted)`
- **Rich rendering** — syntax highlighting, diffs, color schemes
- **Incremental saving** — conversation blocks are saved as they're generated
- **Live streaming** — text/reasoning chunks rendered in real-time with color transitions and mode switches
- **Peek tool calls** — LLM can inspect files without polluting visible history; results cleaned at next turn. File writes/patches always execute.
- **Prompt prefix** — colored segments: DarkGray default, DarkYellow (good cache rate), White (bad rate / significant cost)
- **MCP protocol** — Model Context Protocol support (`stdio` / `streamablehttp` / `sse`)
- **Auto-tool events** — peek cleanup notifications shown as compact auto-tool blocks
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
| `/clone` | Clone an agent's history to a new name (two-step: pick source, enter name) |
| `/use` | Switch to another agent (from the list of existing ones) |
| `/stats` | Current agent statistics: blocks by type, input/output tokens, cache rate, cost |
| `/version` | Show Glyphite version |
| `/models` | List available models and switch between them |
| `/reload` | Reload configuration from JSON files |
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
| `search_grep` | Search text inside files |
| `fetch_web` | HTTP request (GET/POST) with text extraction |
| `todo_write` | Create a structured todo list |
| `todo_update` | Update tasks in a todo list (status, priority) — creates a snapshot chain |
| `memory` | Memory management: `stats` (type breakdown), `delete` (soft-delete with optional `cascade`), `recover` (restore with optional `cascade`) |

## Peek tool calls

The LLM can pass `"peek": true` to any tool to mark the call as transient:
- The tool always executes (file writes/patches still apply)
- The result is visible to the LLM during the current turn
- The block is cleaned at the start of the next turn
- File blocks (read/write) and web fetch are also cleaned

Peek tools always execute — write_file always writes the file, patch_file always applies the patch, fetch_web always fetches. Only the displayed/persisted block is transient.

## Models

Supported:
- **DeepSeek** — v4-flash, v4-pro (via DeepSeek API with cache metrics)
- **OpenAI** — via Microsoft.Agents.AI.OpenAI
- Any models compatible with Microsoft.Extensions.AI

## Versioning

The version is stored in `version.txt`. On `dotnet build` in Debug mode, the patch version is auto-incremented. On `dotnet publish -c Release`, the version stays unchanged (the `publish.sh` script bumps it manually).

```bash
glyphite -v       # → 0.3.70
/version          # → Glyphite v0.3.70
```

The greeting shows the version and agent name:
```
Glyphite CLI v0.3.70 — MainAgent 🏠
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
cp ~/.glyphite/backup/glyphite.v0.2.74 ~/.glyphite/glyphite
```

## Testing

Tests directory (`tests/Glyphite.Tests.Unit/`) is referenced in the solution but not yet created — planned for future iterations.

## License

MIT (c) 2026 Gabatawr
