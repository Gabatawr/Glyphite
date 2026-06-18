# Glyphite

**AI agent with tools — right in your terminal.**

Glyphite is a .NET console-based AI agent that runs commands, works with files, searches for information, manages todos, and interacts with external services — all from the command line. Built for agentic workflows with block-based memory, cascading context, and MCP support.

## Features

- **Conversational AI interface** — chat with AI (DeepSeek / OpenAI) directly in the terminal
- **Agent-oriented architecture** — named agents instead of GUID sessions. Each agent has its own history, home directory, and config
- **Built-in tools:**
  - `execute_bash` — shell commands
  - `read_file` / `write_file` / `patch_file` — file operations with diff highlighting
  - `fetch_web` — HTTP requests (per-call HttpClient, no singleton mutation)
  - `search_glob` / `search_grep` — file and content search
  - `todo_write` / `todo_update` — task management within the conversation
  - `memory` — context and memory management (stats, delete, recover blocks)
- **Block-based memory** — full conversation history stored in SQLite with smart deduplication and compression
  - **`ParentNumber` + cascade** — blocks carry parent references; `memory delete` and `recover` cascade through `Data["parentNumber"]` chains
  - **Todo chain** — each `todo_update` snapshots the previous one, forming a forward chain you can clip at any point
  - **Indexed queries** — `idx_blocks_agent_deleted` on `(agent_id, is_deleted)` for fast context loading
- **Rich rendering** — syntax highlighting, diffs, color schemes
- **Incremental saving** — conversation blocks are saved as they're generated
- **Live streaming** — text/reasoning chunks rendered in real-time with color transitions and mode switches
- **Peek tool calls** — LLM can inspect files without polluting visible history; results cleaned at next turn. File writes/patches always execute.
- **Prompt prefix** — colored segments: DarkGray default, DarkYellow (good cache rate), White (bad rate / significant cost)
- **MCP protocol** — Model Context Protocol support (`stdio` / `streamablehttp` / `sse`)
- **Auto-tool events** — peek cleanup notifications shown as compact auto-tool blocks
- **Versioning** — auto-increment patch on every debug build, rollover at >99, version shown in greeting and via `-v`

## Project structure

```
Glyphite/
├── src/
│   ├── Glyphite.Abstractions/    # Interfaces & models, zero deps (except MEAI)
│   │   ├── Interfaces/               # IAgentManager, IMemoryStore, ITurnProcessor...
│   │   └── Models/                   # MemoryBlock, TurnEvent, Configuration, ChatRequest
│   ├── Glyphite.Host/            # Core: services, tools, data, memory
│   │   ├── Data/                     # SQLite storage (MemoryStore — blocks, config, sessions)
│   │   ├── DI/                       # MSDI wiring (HostServiceCollectionExtensions)
│   │   ├── Memory/                   # BlockMemoryProvider (core, context assembly, metrics)
│   │   ├── Services/                 # TurnProcessor, FailSafeChatClient, ConfigService,
│   │   │                             # BashSessionManager, McpService, ContentDedup,
│   │   ├── Tools/                    # BashTool, FileReadTool, FileWriteTool, FilePatchTool,
│   │   │                             # SearchTools, WebFetchTool, TodoTool, MemoryTool
│   │   └── Utils/                    # OSHelper (platform detection)
│   └── Glyphite.Cli/             # Console client
│       ├── Services/                 # ConsoleRenderer
│       ├── ChatRepl.cs               # Main REPL loop
│       ├── ChatRepl.Commands.cs      # /command handlers
│       ├── ChatRepl.Input.cs         # Input history + suggestion
│       ├── ChatRepl.Streaming.cs     # Live chunk rendering
│       ├── Program.cs                # Entry point (+ `-v` / `--version`)
│       ├── appsettings.json          # Embedded base config
│       └── system-prompt.md          # Agent system prompt
├── tests/
│   └── Glyphite.Tests.Unit/      # Unit tests (planned, not yet created)
├── Glyphite.slnx                   # Solution file (.slnx format)
├── AGENTS.md                       # Agent workflow description
├── Directory.Build.targets         # MSBuild target: auto-increment version
├── bump_version.py                 # Version management script
├── version.txt                     # Current version (v0.3.37)
├── publish.sh                      # Single-file publish with auto-backup
├── Glyphite.json                   # Local config (in .gitignore)
├── .gitignore
├── LICENSE
└── README.md
```

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

## Commands

| Command | Description |
|---------|-------------|
| `/new` | Create a new agent / reset an existing one |
| `/clone` | Clone an agent's history to a new name (two-step: pick source, enter name) |
| `/use` | Switch to another agent (from the list of existing ones) |
| `/stats` | Current agent statistics: blocks by type, tokens, context usage |

| `/version` | Show Glyphite version |
| `/models` | List available models and switch between them |
| `/reload` | Reload configuration from JSON files |
| `/exit` | Exit |

## Tools

All tools are available to the AI agent and can be invoked in conversation:

| Tool | Description |
|------|-------------|
| `execute_bash` | Execute shell commands with timeout and output limits |
| `read_file` | Read file contents (supports compression/dedup for large files) |
| `write_file` | Create / overwrite a file |
| `patch_file` | Partially modify a file (with diff highlighting) |
| `search_glob` | Find files by glob pattern |
| `search_grep` | Search text inside files |
| `fetch_web` | HTTP request (GET/POST) with text extraction |
| `todo_write` | Create a structured todo list |
| `todo_update` | Update tasks in a todo list (status, priority) — creates a snapshot chain |
| `memory` | Memory management: `stats` (type/token breakdown), `delete` (soft-delete with optional `cascade=true`), `recover` (restore with optional `cascade=true`) |

## Models

Supported:
- **DeepSeek** — v4-flash, v4-pro (via DeepSeek API with cache metrics)
- **OpenAI** — via Microsoft.Agents.AI.OpenAI
- Any models compatible with Microsoft.Extensions.AI

## Configuration

Configuration is loaded in a cascading order:
1. `appsettings.json` (embedded in the binary)
2. `Glyphite.json` (global settings in cwd)
3. `Glyphite.{agentName}.json` (agent-specific settings in cwd)

When switching agents via `/use`, configs from cwd are layered on top. If the agent is in its home directory, changes are persisted to the database.

Configuration keys:

| Key | Description | Default |
|-----|-------------|---------|
| `DeepSeek:ApiKey` | DeepSeek API key | — |
| `DeepSeek:Model` | Model name | `deepseek-v4-flash` |
| `DeepSeek:Endpoint` | API URL | `https://api.deepseek.com/v1` |
| `DeepSeek:ContextWindow` | Max context tokens | `1000000` |
| `Agent:MaxToolIterations` | Max tool iterations per turn | `100` |
| `Agent:PeekReasoning` | Mark reasoning blocks as peek | `true` |
| `Agent:PeekToolReasoning` | Mark reasoning during tool iterations as peek | `false` |
| `Bash:DefaultTimeoutMs` | Bash command timeout | `120000` |
| `Bash:ForbiddenCommands` | Forbidden commands | `["sudo"]` |
| `Bash:MaxOutputBytes` | Max output captured | `1048576` |
| `Data:Directory` | Database directory | `data` |
| `Data:DatabaseFileName` | Database file name | `Glyphite.db` |
| `Compression:AutoCompress` | Auto-compress output | `false` |
| `Compression:AutoThreshold` | Context % threshold for warnings | `20` |
| `Compression:CostSignificantThreshold` | +$ turns white when cost ≥ N | `0.01` |
| `ContentDedup:MinLines` | Min lines to trigger dedup | `3` |
| `ContentDedup:FrequencyThreshold` | Line frequency threshold | `0.05` |
| `ContentDedup:MinLineLength` | Min line length for dedup | `32` |
| `ContentDedup:MaxAliases` | Max dedup aliases per block | `10` |
| `WebFetch:Timeout` | HTTP request timeout | `30` |
| `WebFetch:MaxContentLength` | Max response content | `32768` |
| `WebFetch:DefaultFormat` | Response format | `text` |
| `Todo:DefaultStatus` | Default todo status | `pending` |
| `Todo:DefaultPriority` | Default todo priority | `medium` |
| `ToolStreaming:ToolMaxLength` | Per-tool max display length | `execute_bash: -1, read_file: 0, patch_file: 0, peek_clean: -1, fetch_web: 0` |
| `Memory:ProtectedBlockTypes` | Block types protected from deletion | `["agent_data", "user_message", "agent_message"]` |

## Peek tool calls

The LLM can pass `"peek": true` to any tool to mark the call as transient:
- The tool always executes (file writes/patches still apply)
- The result is visible to the LLM during the current turn
- The block is cleaned at the start of the next turn
- File blocks (read/write) and web fetch are also cleaned

Peek tools always execute — write_file always writes the file, patch_file always applies the patch, fetch_web always fetches. Only the displayed/persisted block is transient.

## Versioning

The version is stored in `version.txt`. On `dotnet build` in Debug mode, the patch version is auto-incremented. On `dotnet publish -c Release`, the version stays unchanged (the `publish.sh` script bumps it manually).

```bash
glyphite -v       # → 0.3.61
/version          # → Glyphite v0.3.61
```

The greeting shows the version and agent name:
```
Glyphite CLI v0.3.61 — MainAgent 🏠
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
