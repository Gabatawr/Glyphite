# Glyphite

**AI agent with tools — right in your terminal.**

Glyphite is a .NET console-based AI agent that runs commands, works with files, searches for information, manages todos, and interacts with external services — all from the command line.

## ✨ Features

- **🗣️ Conversational AI interface** — chat with AI (DeepSeek / OpenAI) directly in the terminal
- **🤖 Agent-oriented architecture** — named agents instead of GUID sessions. Each agent has its own history, home directory, and config
- **🔧 Built-in tools:**
  - `execute_bash` — shell commands
  - `read_file` / `write_file` / `patch_file` — file operations with diff highlighting
  - `fetch_web` — HTTP requests
  - `search_glob` / `search_grep` — file and content search
  - `todo_write` / `todo_update` — task management within the conversation
  - `memory` — context and memory management (stats, delete, recover)
- **🧠 Block-based memory** — full conversation history stored in SQLite with smart deduplication and compression
- **📊 Rich rendering** — syntax highlighting, diffs, color schemes
- **📝 Incremental saving** — conversation blocks are saved as they're generated
- **🔌 MCP protocol** — Model Context Protocol support for external tool integration
- **🔢 Versioning** — auto-increment patch on every build, version shown in greeting and via `-v`

## 🏗️ Project structure

```
Glyphite/
├── src/
│   ├── Glyphite.Host/           # Core: memory, tools, providers, services
│   │   ├── Data/                    # SQLite storage (blocks, configs, migrations)
│   │   ├── Memory/                  # Block-based memory and context
│   │   ├── Models/                  # Data models and configuration
│   │   ├── Services/                # Services (bash, config, agents, MCP)
│   │   ├── Tools/                   # Tools (bash, files, search, todo, web, memory)
│   │   └── Utils/                   # Utilities (OS, paths)
│   └── Glyphite.Cli/             # Console client
│       ├── Services/                # Rendering, suggestions
│       ├── ChatRepl.cs              # Main REPL loop
│       ├── Program.cs               # Entry point (+ `-v` / `--version`)
│       └── system-prompt.md         # Agent system prompt
├── tests/
│   └── Glyphite.Tests.Unit/      # Unit tests
├── Glyphite.slnx                   # Solution file
├── AGENTS.md                       # Agent workflow description
├── Directory.Build.targets         # MSBuild target: auto-increment version
├── bump_version.py                 # Version management script
├── version.txt                     # Current version (v0.1.9)
├── publish.sh                      # Single-file publish with auto-backup
├── Glyphite.json                   # Local config (in .gitignore)
├── .gitignore
├── LICENSE
└── README.md
```

## 🚀 Quick start

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

## 🎮 Commands

| Command | Description |
|---------|-------------|
| `/new` | Create a new agent / reset an existing one |
| `/fork` | Clone an agent with a new name (two-step source selection) |
| `/use` | Switch to another agent (from the list of existing ones) |
| `/stats` | Current agent statistics: blocks, tokens, types |
| `/version` | Show Glyphite version |
| `/models` | List available models |
| `/reload` | Reload configuration |
| `/exit` | Exit |

## 🛠️ Tools

All tools are available to the AI agent and can be invoked in conversation:

| Tool | Description |
|------|-------------|
| `execute_bash` | Execute shell commands |
| `read_file` | Read file contents |
| `write_file` | Create / overwrite a file |
| `patch_file` | Partially modify a file (with diff highlighting) |
| `search_glob` | Find files by glob pattern |
| `search_grep` | Search text inside files |
| `fetch_web` | HTTP request (GET/POST) |
| `todo_write` | Create a todo list |
| `todo_update` | Update tasks in a todo list |
| `memory` | Memory management: `stats`, `delete`, `recover` |

## 🧠 Models

Supported:
- **DeepSeek** — v4-flash, v4-pro (via DeepSeek API)
- **OpenAI** — via Microsoft.Agents.AI.OpenAI
- Any models compatible with Microsoft.Extensions.AI

## ⚙️ Configuration

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
| `Agent:MaxToolIterations` | Max tool iterations | `40` |
| `Bash:DefaultTimeoutMs` | Bash command timeout | `120000` |
| `Bash:ForbiddenCommands` | Forbidden commands | `["sudo"]` |
| `Data:Directory` | Database directory | `data` |
| `Data:DatabaseFileName` | Database file name | `Glyphite.db` |
| `Compression:AutoCompress` | Auto-compress output | `false` |

## 🔄 Versioning

The version is stored in `version.txt`. On `dotnet build` in Debug mode, the patch version is auto-incremented. On `dotnet publish -c Release`, the version stays unchanged.

```bash
glyphite -v       # → 0.1.9
/version          # → Glyphite v0.1.9
```

The greeting shows the version and agent name:
```
Glyphite CLI v0.1.9 — Glyphite 🏠
```

## 📦 Publishing and backups

Use `./publish.sh` to publish:

```bash
./publish.sh
# 1. Archives the previous version → ~/.glyphite/backup/glyphite.v{version}
# 2. Saves the current binary as .prev
# 3. Publishes a new single-file binary
```

Rollback:
```bash
cp ~/.glyphite/backup/glyphite.v0.1.8 ~/.glyphite/glyphite
```

## 🧪 Testing

```bash
dotnet test tests/Glyphite.Tests.Unit
```

## 📄 License

MIT © 2026 Gabatawr
