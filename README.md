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

- **Conversational AI interface** — chat with an AI agent (any OpenAI-compatible provider) directly in
  the terminal.
- **Agent-oriented architecture** — named agents instead of GUID sessions. Each agent has its own
  history, home directory, and config. `SessionManager` centralizes the lifecycle (create, clone,
  switch, delete, resume, per-agent hot-reload); `InputHistory` is shared across agents.
- **Built-in tools** — `bash` + `bash_back`, `read_file` / `write_file` / `patch_file`, `view_image`,
  `fetch_web`, `search_glob` / `search_grep`, `todo`, `kvstore`, the `pocket_*` family, `memory`, and
  `subagent_run` / `subagent_use` / `subagent_list`. See [Tools](#tools).
- **Image support** — reference an image path or URL and it is attached automatically, or the model
  pulls one itself with `view_image`. Format is detected from the bytes, provider limits are enforced
  before the request. See [Images](#images).
- **Subagents** — delegate tasks to ephemeral (`subagent_run`) or persistent (`subagent_use`) worker
  agents, including in parallel. See [Subagent architecture](#subagent-architecture).
- **Pocket tools** — user-defined tool aliases: bash command templates with typed argument schemas,
  materializable into native tools. See [Pocket tools](#pocket-tools).
- **MCP protocol** — Model Context Protocol support (`stdio` / `streamablehttp` / `sse`), per-agent
  servers, `{serverName}_` tool prefixing, per-server/per-tool execution settings. See
  [MCP](#mcp-model-context-protocol).
- **Block-based memory** — full conversation history in SQLite with indexed context loading and smart
  deduplication. The **todo chain** keeps only one active list, each update snapshotting the previous.
- **Atomic auto-compaction** — two selectable strategies (`fibo`, `struct`) summarize old history via
  the LLM, replacing it atomically; on failure the original blocks stay intact. Reasoning blocks are
  compacted separately. See [Auto-compaction](#auto-compaction).
- **Config hot-reload per turn** — every `Glyphite.json` section is re-read on the next user turn; MCP
  servers reconnect on hash change. See [Config hot-reload](#config-hot-reload).
- **Tool display & execution control** — `ToolStreaming` (console) and `ToolExecution` (server-side
  truncation, per-tool peek/timeout/maxSize), both per-tool and wildcard-capable. See
  [ToolStreaming](#toolstreaming) and [ToolExecution](#toolexecution).
- **Safety controls** — interactive `[OK] [Stop] [Check]` confirmation for dangerous commands, an
  LLM-powered `SafetyChecker`, and `peek` for transient tool results. See
  [Safety](#interactive-confirmation-for-dangerous-commands).
- **Content deduplication** — repeated lines compressed in bash, read, and search outputs.
- **Rich rendering** — syntax highlighting, diffs, color-coded tool rendering, and automatic markdown
  table formatting (proportional columns, multi-line cells, word-wrap) in streaming, replay, and
  subagent results.
- **Incremental saving & live streaming** — blocks are persisted as they are generated; text and
  reasoning chunks render in real time.
- **Structured file logging** — all host service logs go to `~/.glyphite/logs/{date}-{run}.log` via
  Serilog + `ILogger<T>`, with no subagent console noise. See [Logging](#logging).
- **CLI quality of life** — tab completion for `/` commands, inline agent args (`/new MyAgent`),
  colored prompt segments (cache rate, cost), and version display.

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

The first launch creates a global config template at `~/.glyphite/Glyphite.json` — set `LLM:ApiKey`
there once, and `glyphite` works from any directory without per-folder config files. The global file is
a **one-time snapshot**: it is never overwritten, so new keys added later to the source
`appsettings.json` flow through automatically, but **changed values of existing keys do not** — the
global file wins. To pick up a changed default, edit `~/.glyphite/Glyphite.json` manually or delete it
(it is recreated from the embedded template on next launch; you will need to re-enter the API key).

First-run flow: when no API key is configured anywhere, Glyphite asks how to proceed — enter the key
(saved to the global config), create a local `Glyphite.json` in the current folder (the API key is
prompted and written into it right away), or skip. When a global key exists and you launch from a new
folder, it asks once whether to use the global config or create a local one (creating a local file also
prompts for the key); the choice is remembered in `state.json` next to the global config, so the prompt
appears only once per folder. Non-interactive runs (redirected stdin) silently use the global config. A
config file in the working directory is created **only** through this prompt — never automatically.

On first launch, Glyphite asks for an agent name. On subsequent launches, it resumes the last active
agent for the current directory.

Configuration is loaded in cascading order: `appsettings.json` (embedded defaults) →
`~/.glyphite/Glyphite.json` (global — install location, shared across all working directories) →
`Glyphite.json` in the working directory (per-project overrides) → `Glyphite.{agentName}.json`
(agent-specific).

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
| `bash` | Execute shell commands with timeout and output limits. `back=true` runs as a background process and returns a taskId immediately. Commands matching `CheckRequireCommands` trigger the interactive confirmation panel |
| `bash_back` | Manage background bash tasks: `list`, `wait` (block until done, kills on timeout), `partial` (poll current output without killing) |
| `read_file` | Read a file with line numbers, `offset`/`limit` for partial reads, auto-dedup for logs, and git awareness — lines differing from `HEAD` are marked with `+` (plus a summary in the header), and `git_delete=true` appends the deleted lines from the diff |
| `write_file` | Create / overwrite a file |
| `patch_file` | Partially modify a file (with diff highlighting). A failed match reports the approximate location |
| `view_image` | Load a picture (local path, `http(s)` URL, `file://` or `data:` URL) so the model can actually see it. See [Images](#images) |
| `search_glob` | Find files by glob pattern |
| `search_grep` | Search text inside files, with `around` for N context lines per match and content dedup |
| `fetch_web` | HTTP request (GET/POST) with text extraction. Refuses images with a pointer to `view_image` |
| `kvstore` | Key-value store for agent data. Two scopes: `vault` (persistent table) and `config` (agent config + session overrides). `get`/`set` with glob masks (`*`/`?`), TTL (vault only), dry-run confirm flow for masked sets. Empty value = delete. Ephemeral (subagent_run) agents use memory only — no DB leaks |
| `todo` | Create, update, or list todo lists — title as immutable ID for multi-list support. `create(title, items)`, `update(title, items)`, `list(title?)`. Statuses: pending, in_progress, done, cancelled, blocked. Update by index or by text (no index = match by text, new text = add item) |
| `memory` | Memory statistics: `stats` (block type distribution, token usage, cache stats, cost) |
| `pocket_list` | List pocket tools — user-defined aliases stored in the agent's vault. Compact by default, `details=true` for full cards. Glob filter on name, exact filter on category. Shows local + global entries (`[global]`, `[shadowed]` markers) |
| `pocket_add` | Add a pocket tool: `name`, `desc`, bash `cmd` template with `{arg}` placeholders, optional `args` schema (JSON array with `req`/`def`/`raw`), `cwd`, `category`. Options: `materialize` (native tool on next turn), `scope` (`local`/`global`). Safety preflight at add |
| `pocket_set` | Update one field of an existing pocket tool: `materialize`, `name` (rename), `scope` (move local↔global), `category`, `args`, `cwd`, `desc`, `cmd` (cmd change re-runs the safety preflight) |
| `pocket_remove` | Remove a pocket tool — effective entry by default (removes a local shadow, the global stays); `scope="global"` removes the shared entry |
| `pocket_run` | Execute a pocket tool immediately by name (same-turn, no reload needed) |
| `subagent_run` | One-shot task execution (ephemeral). Auto-GUID temp agent by default; with a name + existing agent it is a dry-run (delta cleaned after). Ephemeral: usage restored to pre-run state, no compaction, blocks deleted after. Supports `mode="parallel"` |
| `subagent_use` | Execute a task on a named subagent (auto-created if not found). Memory and context **accumulate** across calls. Supports `mode="parallel"` |
| `subagent_list` | List all existing agents (excluding current session) with home, model, block count, cache stats |

Any MCP-connected server's tools also become available automatically.

## Pocket tools

Pocket tools are user-defined tool aliases stored in the agent's KV vault. Each entry wraps a bash
command template with a typed argument schema — a way to give the agent reusable custom tools without
code changes.

### Storage

Entries live in `kv_store` under keys `pocket.<name>` (JSON values). Local entries are scoped to the
agent's vault; global entries use the shared sentinel namespace (`agent_id = "__global__"`) and are
visible to **all** agents.

### Materialization

- Entries with `materialize=true` (favorites) are exposed as **native typed tools** on the next tool
  load — named `<name>_pocket` so their origin is explicit (`git_st` → `git_st_pocket`).
- The suffix removes collisions with builtins: a pocket entry named `bash` materializes as `bash_pocket`.
- Non-materialized entries are executed on demand via `pocket_run`.
- The toolset is rebuilt every turn, so materialization takes effect automatically — no restart needed.

### Scope: local vs global

| Scope | Stored | Visible to |
|-------|--------|------------|
| `local` (default) | agent's vault | only this agent |
| `global` | shared namespace (`__global__`) | all agents — visible, executable, editable |

- **Shadows** — a local entry with the same name takes precedence over the global one for this agent
  (in `pocket_run`, editing, and materialization). The global stays intact for others.
- **Editing** — `pocket_set` / `pocket_remove` operate on the effective entry by default (local first,
  else global); pass `scope="global"` to edit the shared entry explicitly, even when shadowed.
- **Materialization is independent of scope** — a global entry with `materialize=true` becomes a native
  tool for **all** agents (no per-agent flags).
- **Moving** — `pocket_set X scope=global` moves a local entry to the shared space (and back); a
  collision in the target scope blocks the move.

### Safety

Command templates are safety-checked **once at add time**: forbidden-command/directory hard blocks,
plus an interactive confirmation prompt when the template matches `CheckRequireCommands`. Approved
entries skip the interactive prompt on execution (hard blocks still apply at run time). Changing `cmd`
via `pocket_set` re-runs the preflight; a rejected template leaves the entry unchanged.

## Subagent architecture

Subagents enable the main agent to delegate tasks to specialized worker agents.

### `subagent_run` — one-shot temporary execution

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

The subagent has access to the `memory` tool (`stats`), allowing it to inspect memory statistics.
Subagents **do not** have access to `subagent_*` tools (prevents recursive agent creation).

### Escape cancellation

When the user presses Escape during a subagent task:

1. CancellationToken propagates through the **entire chain**: tool lambda → `RunAgentTask` →
   `TurnProcessor.ProcessAsync`
2. The subagent's `ProcessAsync` throws `OperationCanceledException`
3. **Dry-clean runs**: for `subagent_run` on a named agent, blocks created during this run are deleted
   and usage is cleared
4. **`pending_runs` record is cleared** — no orphan agents left in DB
5. **Scope is disposed** — `SubAgentManager` removes the entry, semaphore released
6. For GUID temp agents — `DeleteSessionAsync` runs in `finally`, agent entirely removed
7. **Usage from completed iterations is already saved** (per-iteration write via `OnIterationRecorded`)

### Crash safety (`pending_runs` table)

If the process crashes during a subagent task, a record in the `pending_runs` table persists in SQLite:

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
- Tools with `mode="sequential"` (default) or without `mode` run one at a time
- If two parallel calls use the **same agent name**, they are split into sequential groups to prevent
  race conditions
- Parallel-safe tools: `read_file`, `view_image`, `fetch_web`, `search_glob`, `search_grep`,
  `subagent_use`, `subagent_run`

### Config loading

Both CLI agents and subagents use the unified `ISessionConfigLoader` (old `ConfigLoader.cs` removed).
Called every turn:

1. **Home → DB** — config files from the agent's home directory are read, compared with existing DB
   keys (change detection), and only home-originated keys are persisted to DB.
2. **Cascade merge** — `DB session keys` → `parentCwd/Glyphite.json` →
   `parentCwd/Glyphite.{id}.json` → `agentCwd/Glyphite.json` → `agentCwd/Glyphite.{id}.json` (each
   overrides the previous).
3. **Auto-migrate** — if the original home directory was deleted, the current working directory is
   adopted as the new home.
4. **Overlay** — if the agent is not at home, the merged config is set as a session overlay; otherwise
   `IConfiguration` + DB suffice.

## Images

The agent can see pictures. Two ways in, one delivery mechanism.

```
> what is on /home/me/shot.png?               ← attached automatically (path exists, sniffs as an image)
> compare with https://cdn.example.com/a.png  ← attached automatically (URL looks like an image)
> look at the screenshot in "my shot.png"     ← quoted paths survive spaces

# or the model pulls one itself:
view_image(source: "/tmp/chart.webp", question: "what is the trend here?")
```

### `view_image`

| Argument | Meaning |
|----------|---------|
| `source` | Local path, `http(s)` URL, `file://` URL or `data:` URL |
| `detail` | `auto` (default), `low` (provider downscales to 512×512), `high`, `original` |
| `question` | Optional — written next to the image to keep the request focused |

The tool does **not** return the image. A tool result is a `tool`-role message, and the provider
rejects images outside `user` messages — so the picture is queued on a per-scope `ImageAttachmentSink`
and `FailSafeChatClient` injects it as one user message after the tool batch. The model sees it on the
very next iteration.

### Automatic attachment

`TurnProcessor` scans each user message before sending it:

- **Local paths** must have a known image extension *and* exist *and* sniff as a real image — a `.txt`
  mentioned in the message is never opened.
- **URLs** must end in an image extension by default (see `UrlMatching`).
- **Quoted runs** are scanned first, so paths containing spaces work: `"my shot.png"`.

Only references that are certainly images qualify — a wrong attachment is worse than a missed one. A
broken reference is logged and the turn continues untouched.

### Where the images go

| | |
|---|---|
| In the request | A `user` message part — the API accepts images in no other role |
| In memory | A cheap text marker — `[image attached: path (image/png, 1920×1080, 412 KB)]` — instead of base64, so history stays small and the agent can re-open the picture later with `view_image` |
| On screen | `[image: …]` before the model is called |

### The same picture, once per turn

Both ways in share one budget and one identity. A picture is keyed by its resolved path, its URL, or —
for a `data:` URL — a hash of its bytes, so the same file spelled two different ways is still
recognised as one picture. Something already riding in the conversation is never attached twice:
`view_image` on an image the user's own message already attached answers *"already attached — look at
it directly"* and sends nothing.

The image count (`MaxImagesPerRequest`) and the total inline bytes (`MaxTotalBytes`) cover **both**
ways in, so the provider never receives a request past its limits — an over-budget tool call comes
back as a plain error the model can react to instead of killing the turn.

### One way in, by design

Reading a picture as text produces junk, so both text tools refuse one and name the tool that can
actually show it:

| Tool | Answer on an image |
|------|--------------------|
| `read_file` | `Error: … is an image (image/png, 1920×1080, 412 KB), not text — read_file returns text. Use view_image to look at it.` |
| `fetch_web` | `Error: <url> is an image (image/png, 1920×1080, 412 KB), not a document — fetch_web returns text. If you really need to look at it, open it explicitly with view_image.` |

`fetch_web` decides from the body bytes first and only then from the declared `Content-Type`, so a
server that mislabels an image is still caught, while a `.png` URL that actually serves HTML stays a
document. SVG is text and is not refused. `search_grep` already skips images — known extensions plus a
NUL-byte check over the first 1 KB.

### Configuration

```json
"Image": {
  "Enabled": true,
  "AutoAttach": true,
  "UrlMode": "auto",
  "UrlMatching": "extension",
  "Detail": "auto",
  "MaxImageBytes": 33554432,
  "MaxTotalBytes": 41943040,
  "MaxImagesPerRequest": 10,
  "MaxUrlLength": 8192,
  "MaxDimension": 8192,
  "DownloadTimeoutSeconds": 60,
  "Extensions": [".png", ".jpg", ".jpeg", ".gif", ".webp"]
}
```

| Key | Meaning |
|-----|---------|
| `Enabled` | Master switch — off makes `view_image` refuse and disables auto-attach |
| `AutoAttach` | Scan user messages for image references |
| `UrlMode` | How an `http(s)` image reaches the model. `auto` (default) is adaptive — a public URL is handed to the provider, which downloads it (no bytes through the agent), while a host the provider can never reach is downloaded here and inlined: `localhost`, `10.x`, `192.168.x`, `172.16-31.x`, `169.254.x`, `.local`, `.lan`, `.internal`, bare intranet names. `inline` always downloads. Mode names are matched case-insensitively |
| `UrlMatching` | What a URL must look like to be auto-attached: `extension` (default), `always`, `never` |
| `Detail` | `auto` omits the field entirely; `low` / `high` / `original` are sent as `image_url.detail` |

Size, count, URL-length and dimension defaults mirror the provider's documented limits, so requests
stay valid without tuning.

> **Worth knowing:** the container is detected from the bytes, never from the file name — a PNG named
> `.txt` is still a PNG. URLs past `MaxUrlLength` are inlined regardless of mode, because the provider
> would reject them. Each image costs tokens (up to ~1024 per image after provider-side resizing,
> largely independent of file size). With `auto`, a URL the provider cannot fetch (404, auth, hotlink
> protection) fails the **whole request** with an opaque 400 — use `inline` when sparing bandwidth
> matters less than that risk. `MaxImagesPerRequest` and `MaxTotalBytes` are enforced on **both** ways
> in — images attached from your message and ones a tool pulls with `view_image`; an over-budget tool
> call comes back as a plain error instead of killing the turn.

## Auto-compaction

When enabled, Glyphite automatically compresses old conversation history via LLM summarization. Two
strategies are available, toggled via the `Strategies` dictionary flags:

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

- **Trigger:** when the last request's tokens exceed `AutoThreshold`% of the context window (e.g. 20%
  of 1M = 200K tokens)
- **Strategy selection:** if one strategy is enabled — it's used. If both — one is picked randomly each
  turn (visible in `[AutoTool: compression]`)

### `fibo` (Fibonacci zones)

- History is grouped into Fibonacci-sized zones (1, 1, 2, 3, 5, 8... turns) from newest to oldest.
- **Zones 1-2** (the 1+1 newest turns) are preserved intact — **all blocks**, including tool calls,
  auto_tool results, reasoning.
- **Zones 3+** are fully sent to the LLM (all block types), each zone summarized **in parallel** with
  the structured template: `## Topics / Key Actions / Results / State Changes / Open & Carried Over`.
- Subagent tools (`subagent_run`/`subagent_use`) are preserved in the summarization.

### `struct` (structured cut)

- Every block from `agent_data` (exclusive) to the end — **unfiltered** — is sent to the LLM in **one**
  call with the structured template: `## Goal / Progress / Key Decisions / Relevant Files / Next Steps`.
- The LLM sees the full picture (all block types — tool calls, results, reasoning, turn messages),
  producing a single comprehensive summary that covers **everything**, including the last 2 preserved
  turns.
- The summary block is placed **after** the preserved zones (last 2 turns). Order in DB:
  `agent_data → preserved turns → struct summary`.
- All old blocks (except `agent_data`) are replaced atomically by the new summary.

### Common

- **Fail-safe:** if summarization fails, old blocks are kept intact (no data loss)
- **Atomic replacement:** summaries + preserved blocks are inserted atomically via
  `ReplaceBlocksSinceAsync` in a single SQLite transaction. On crash — rollback, nothing lost.
- **Usage tracking:** compaction LLM calls record hit/miss/output tokens to session stats
- **Notification:** `[AutoTool: compression | {"AutoCompress":true,"Strategy":"fibo",...}]` shown
  before the LLM call
- **No UI freeze:** the notification appears immediately, then the slow summarization runs in the
  background

### Reasoning auto-compaction

Large `agent_reasoning` blocks are compacted separately, after each turn. This replaces the old "peek
reasoning" system.

```json
"Compression": {
  "AutoCompressReasoning": true,
  "AutoCompressReasoningMaxSize": 6000
}
```

- **Trigger:** after each turn, all `agent_reasoning` blocks > `AutoCompressReasoningMaxSize` chars are
  compacted
- **Process:** blocks are compacted **in parallel** by the LLM, each reduced to ~`maxSize/4/2` tokens
  (~750 chars)
- **Result:** a new summary `agent_reasoning` block replaces the original; `AutoTool: compress_reasoning`
  is shown inline
- **Usage recorded:** compaction LLM calls save hit/miss/output to session stats

## MCP (Model Context Protocol)

Glyphite supports MCP servers via `stdio`, `streamablehttp`, and `sse` transports. Configure servers in
`Glyphite.json`:

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

**Tool name prefixing:** MCP tools are prefixed with `{serverName}_` to avoid name collisions. For
example, a tool `explore` from server `codegraph` becomes `codegraph_explore`. Use the prefixed name in
`ToolStreaming`, `ToolExecution`, and `McpExecution` configuration. The prefix is stripped before the
call reaches the MCP server — the server always sees the original tool name.

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

**Hot-reload:** when the MCP server config changes, `McpService` detects the hash change and reconnects
automatically on the next turn. No restart needed.

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
| `Image.*` | Image limits and URL handling | per-call via `ImageTool` / per-turn via `TurnProcessor` |

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

- **`MaxSize`**: `-1` full output (default), `0` hidden from console (LLM still sees everything),
  `N > 0` first N characters
- **`HiddenArgs`**: list of argument names to hide from console output (e.g. file content)

Works for all tools including MCP (use a prefixed name like `codegraph_explore`). Wildcard `*` matches
any tool name suffix (e.g. `codegraph_*` matches all tools from the `codegraph` server). The first
matching rule wins. Changes are picked up per-turn without restart.

> **Note:** `ToolStreaming` and `ToolExecution` control **independent** MaxSize values.
> `ToolStreaming.MaxSize` controls what's shown in the console; `ToolExecution.MaxSize` controls what's
> sent to the LLM server-side. You can hide output from the console while still showing full output to
> the LLM, or vice versa.

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

- **`Peek`** — whether the tool result is marked as peek by default (the LLM can still override via
  `extra_cfg.peek`)
- **`Timeout`** — max execution time in seconds before the tool is cancelled. Applied via a
  `CancellationTokenSource` linked to the caller's token
- **`MaxSize`** — max output characters. When exceeded:
  - Full output is saved to a temp file: `{tmpDir}/{agentId}/{toolName}_{timestamp}.out`
  - The truncated view shows **1/3 from top + truncation notice + 2/3 from bottom** (so the LLM sees
    both the beginning and the end)
  - If no `tmpDir` is configured (subagents), simple inlined truncation with a length note

The LLM can override any setting per-call via `extra_cfg` (e.g. `"extra_cfg": { "peek": true, "timeout": 600 }`).

Works for all tools including MCP — use the prefixed tool name (e.g. `codegraph_explore`) or a wildcard
suffix (e.g. `codegraph_*`).

## Interactive confirmation for dangerous commands

When the model calls `bash` with a command matching `CheckRequireCommands`, an interactive panel
appears in the console:

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

The last selected choice is saved per-agent in KVStore under `confirmation_last_choice`. On the next
confirmation prompt, the saved choice is pre-selected.

### Subagent behavior

Subagents (`subagent_run` / `subagent_use`) **skip the panel** entirely — the `isSubAgent` flag causes
an immediate return of `Choice.Check`. The command goes directly to `SafetyChecker` for LLM evaluation.

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

`CheckRequireCommandsTimeout` — seconds before the timer auto-selects the highlighted choice (default
10, set to 5 in the shipped defaults).

## SafetyChecker

LLM-powered safety evaluation for shell commands. Used when the user selects **Check** in the
interactive confirmation panel, or automatically for subagents.

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

- **Context-aware:** SafetyChecker reads blocks from the last `turn` marker (or `user_message` /
  `agent_task` fallback) to the end — the full current turn context
- **Usage recorded:** hit/miss/output tokens are saved to session stats via `UsageParser.Normalize`
- **Fallback to safe:** on LLM failure or malformed JSON, defaults to `allow: true` with a note
- **No blocking on errors:** if the LLM call fails, the command proceeds

## Peek tool calls

The LLM can pass `"peek": true` to any tool to mark the result as transient. Per-tool peek defaults can
be configured via `ToolExecution`:

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
- Reasoning blocks are **not** peek-tagged anymore — they are auto-compacted via
  `Compression.AutoCompressReasoning`

**How it works:** `FailSafeChatClient` tracks pending peek call ids during tool execution. After the LLM
consumes the results (reads them and generates a response), it replaces the real data with `(peek)` in
the message list. The LLM sees the data once, then sees only `(peek)` on subsequent iterations.

> Peek is for inspection — use it to read files, check command output, or fetch web pages without
> cluttering the conversation history.

## Models

Glyphite uses the OpenAI-compatible API via `Microsoft.Extensions.AI.OpenAI`. Any provider that speaks
the OpenAI protocol works — just configure the endpoint, API key, and model in `Glyphite.json`.

**Tested with:**
- **DeepSeek** — v4-flash, v4-pro (cache metrics via `Usage.InputTokenDetails.CachedTokenCount`)

**Should work (format parsers included):**
- **OpenAI** — cache via `usage.prompt_tokens_details.cached_tokens`
- **Anthropic** — cache via `usage.cache_read_input_tokens`
- **Google Gemini** — cache via `usageMetadata.cachedContentTokenCount` (also offers an
  OpenAI-compatible endpoint)

## Logging

All host service logs are written to structured files via Serilog:

```
~/.glyphite/logs/22-06-2026-1.log
~/.glyphite/logs/22-06-2026-2.log
```

- **Path:** `~/.glyphite/logs/{dd-MM-yyyy}-{run}.log` (auto-rotated per run)
- **Format:** `2026-06-22 12:34:56.789 +00:00 [INF] Turn start session Agent0605, model deepseek-v4-flash`
- **Level:** Information and above (errors, warnings, info)
- **Scope:** All host services via `ILogger<T>` (TurnProcessor, CompactionService, McpService,
  FailSafeChatClient, ConfigService, BashSessionManager)
- **Subagent isolation:** subagent logs go to the same file, but never to console — no UI pollution

Key log events:
- Turn start/end with session ID and usage stats (hit/miss/output)
- Compaction start/end with zone and summary counts
- Tool iteration count and accumulated tokens per turn
- MCP connection/disconnection/reconnection events
- Error conditions (parsing failures, process kill errors, etc.)

## Architecture notes & known limitations

- **Single-writer SQLite** — each agent has its own SQLite database with a single write path (serialized
  via `SemaphoreSlim` in the repositories; WAL mode allows concurrent reads). Only one turn writes at a
  time; concurrent tool writes are funneled through the same lock. Do not open the DB from a second
  process while the CLI is running. `SQLITE_BUSY` is retried rather than surfaced as a failure.
- **Per-agent scoped services** — the CLI creates a DI scope per agent session. `IConfigService`,
  repositories, `ToolRegistry`, `CompactionService`, etc. are scoped to the agent, so config hot-reload
  and tool state never leak across agents. Subagents get their own scope and DB.
- **Compaction failure fallback** — if the summarization LLM call fails, both strategies (`fibo` and
  `struct`) preserve the original turn blocks intact instead of dropping them: the compaction becomes a
  renumbering no-op and no history is lost. Verified by tests.
- **Tool execution defaults** — per-tool `peek` / `maxSize` / `timeout` defaults are centralized in
  `ToolExecutionDefaults` (`src/Glyphite.Host/Tools/`); per-tool overrides come from
  `Glyphite:ToolExecution` (builtin tools) or `Glyphite:McpExecution` (MCP tools). MCP tools get a
  longer default timeout (300s vs 120s for builtins) because they cross process boundaries.
- **Streaming cancellation is graceful** — Escape cancels the outer stream after the current iteration;
  the inner LLM stream runs to completion so partial responses are persisted. Usage is written
  per-iteration, so a crash mid-turn never loses token accounting.
- **Image cost is token-based, not byte-based** — the provider normalizes each image to roughly the same
  token count regardless of file size, and images are not persisted in history (only a text marker), so
  they never inflate long-term context. `MaxImageBytes` / `MaxTotalBytes` bound the request body, not
  the bill.

## Versioning

The version is stored in `version.txt`. On `dotnet build` in Debug mode, the patch version is
auto-incremented (rollover at >99 bumps the minor). On `dotnet publish -c Release`, the version stays
unchanged — `publish.sh` bumps it manually.

```bash
glyphite -v       # → vX.Y.Z
/version          # → Glyphite vX.Y.Z
```

The greeting shows the version and agent name:

```
Glyphite CLI vX.Y.Z — MainAgent 🏠
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
cp ~/.glyphite/backup/glyphite.v{version} ~/.glyphite/glyphite
```

## Testing

Tests live in `tests/Glyphite.Tests.Unit/` — 443 unit tests written with xUnit + NSubstitute covering
the turn pipeline (`TurnProcessor`, `FailSafeChatClient`, `ToolExecutor`), compaction strategies
(`fibo`/`struct` incl. failure fallback), `BashSessionManager`, usage parsing (all provider formats),
`ContentDedup`, configuration validation, the data layer (`SessionRepository`, `BlockRepository`),
`FilePatchTool`, and the image pipeline (formats, loader, spec extraction, tool, wire format).

```bash
dotnet test
```

## License

MIT (c) 2026 Gabatawr
