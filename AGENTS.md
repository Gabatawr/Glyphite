# Agents' workflow

We work from the **published** version of the application — the single-file binary in `~/.glyphite/`.

**I (the agent) build** — I run `dotnet build` and fix any compilation errors.

**You (the user) publish** — when the build is green, you run `./publish.sh` which creates the
single-file binary, backs up the previous version, and updates `~/.glyphite/glyphite`.

After publish — exit and restart glyphite to test the new build.

> **Important:** never modify `appsettings.json` (embedded in binary). Changes to it require a rebuild
> + republish. Use `Glyphite.json` for overrides.

## Configuration hierarchy

Settings are applied in this order (each overrides the previous):

1. **`appsettings.json`** (embedded in the binary) — base defaults. **Do not modify** — it's compiled
   into the binary.
2. **`Glyphite.json`** in current working directory — overrides base defaults. Hot-reloaded via
   `IConfiguration` on every turn.
3. **`Glyphite.{agentName}.json`** in current working directory — agent-specific overrides.

The global `~/.glyphite/Glyphite.json` (created on first launch, a one-time snapshot) sits between the
embedded defaults and the working-directory files — see the README's Quick start for the full order.

### Per-agent config loading (`ISessionConfigLoader.LoadConfigAsync`)

Called **every turn** (both for CLI agents and subagents). Four steps:

```
Step 0 ─ Home directory still exists?
  └─ NO → homePath = agentCwd (current working directory becomes new home),
          SetAgentHomePathAsync updates DB, stale session keys cleared

Step 1 ─ Home → DB (change detection)
  Read home/Glyphite.json + home/Glyphite.{id}.json
  Compare with existing DB session keys
  └─ Changed → DeleteConfigByScope + UpdateConfig (only home keys!)

  ⚠️ Only home-originated keys go to DB. Parent/cwd keys never persist.

Step 2 ─ Final merge (bottom→top, top wins)

  agentCwd/Glyphite.{id}.json       TOP (if cwd != parentCwd)
  agentCwd/Glyphite.json                 (if cwd != parentCwd)
  parentCwd/Glyphite.{id}.json
  parentCwd/Glyphite.json
  DB session keys (home keys only)  BASE

Step 3 ─ Overlay?
  cwd == homePath → no overlay (IConfiguration + DB suffice)
  cwd != homePath → SetSessionOverlay(agentId, merged)
```

**Key principles:**
- Home keys → DB (change detection, only home keys persist)
- Parent + cwd keys → each time from files, never saved to DB
- One loader for CLI agents and subagents
- Auto-migrate home if original directory was deleted

### System instructions (`IInstructionProvider`)

Instructions are built **every turn** as a single string and set via `ChatOptions.Instructions` (for
all agents — CLI and subagent).

**Merge order** (each appended in order):
1. **`system-prompt.md`** (embedded in Glyphite.Host) — always present, cached forever
2. **`AGENTS.md`** (cascade: home → parentCwd → agentCwd, top wins) — only if
   `Memory:ReadAgentsFile: true`
3. **`Glyphite.{agentId}.md`** (cascade: home → parentCwd → agentCwd, top wins) — always if file exists

Configuration (`Memory` section):

| Option | Default | Description |
|--------|---------|-------------|
| `ReadAgentsFile` | `false` | If true, cascade-read `AGENTS.md` and append to instructions |
| `TurnReloadAgentsFile` | `false` | If true, re-read `AGENTS.md` from disk every turn |
| `TurnReloadNameFile` | `false` | If true, re-read `Glyphite.{id}.md` from disk every turn |

**Example usage:**

```json
{
  "Glyphite": {
    "Memory": {
      "ReadAgentsFile": true,
      "TurnReloadNameFile": true
    }
  }
}
```

Create `Glyphite.my-agent.md` in your project root (or agent's home dir):

```markdown
# My Agent Instructions

You are a specialized QA agent. Always run tests before and after changes.
```

> `Glyphite.*.md` files are gitignored — they are per-developer agent settings.

## Architecture

- **Abstractions** — interfaces, models, no deps (except `Microsoft.Extensions.AI`)
  - Includes `ISessionConfigLoader`, `IAgentManager`, `IAgentStore`, `IBlockStore`, `IConfigStore`, etc.
- **Host** — service implementations (TurnProcessor + phases, FailSafeChatClient, ToolExecutor,
  UsageTracker, SessionRepository, BlockRepository, ConfigRepository, BlockMemoryProvider,
  SessionConfigLoader, SubAgentManager, RepositoryBase), tools (SubAgentTool, TodoTool, ToolRegistry,
  ImageTool, PocketTool, etc.), images (`ImageLoader`, `ImageFormats`, `ImageAttachmentSink`), utils
  (UsageParser, BlockTypeIcon, ToolCallHelper), MCP, DI wiring
- **Cli** — UI only (ChatRepl + partials, SessionManager + Commands partial, InputHistory,
  ConsoleRenderer, AgentPicker). No persistence logic. Config loading unified via
  `ISessionConfigLoader` (old `ConfigLoader.cs` removed).

## Peek flow

Two levels of peek cleanup, both in `BlockRepository.cs`:

1. **Inter-iteration** (`ClearPeekMarkersAsync(includeReasoning: false)`) — cleans tool/file peek
   blocks between tool batches (`RemovePeekBlocksAsync` + set `tool_result = NULL`).
2. **Start-of-turn** (`RemovePeekBlocksAsync(includeReasoning: true)`) — cleans ALL peek blocks from
   DB (safety net before new turn).

Both are separate from the `FailSafeClient` messageList cleanup — DB and in-memory are independent.

## Images

`view_image` (`Tools/ImageTool.cs`) loads a picture on demand; `TurnProcessor` also auto-attaches any
image path or URL found in a user message. Both funnel through `ImageLoader` (`Host/Images/`), which
sniffs the container from the magic bytes (never the name), enforces the provider limits, and returns
an `ImagePayload`.

**Delivery is indirect:** the provider accepts images only in `user` messages, and a tool result is a
`tool` message — so a tool cannot return a picture. It queues the payload on the per-scope
`ImageAttachmentSink`, and `FailSafeChatClient` drains the sink after each tool batch into one injected
user message. The sink is `Clear`ed at the start of every turn.

**Two invariants to preserve:**

- **One budget for both ways in.** `ImageAttachmentSink.Seed` registers what the opening message
  already carries; `TryAdd` then enforces `Image:MaxImagesPerRequest` / `Image:MaxTotalBytes`
  cumulatively for the rest of the turn. Over budget → a tool-level error
  (`ImageAddResult.OverBudget`), never an opaque 400 from the provider.
- **One identity per picture.** `ImagePayload.Key` is the resolved absolute path, the URL, or a content
  hash for `data:` URLs. A `view_image` for something already riding in the conversation returns
  `ImageAddResult.AlreadyAttached` and sends nothing — the model already has it.

`read_file` and `fetch_web` refuse an image and point at `view_image` instead of returning junk.

## Schema

```sql
-- Tables:
-- sessions, blocks, kv_store, session_usage, config, pending_runs, agent_launches, schema_version

-- blocks columns:
-- id, agent_id, number, type, created_at, content, tool_name, data, model,
-- tool_result, updated_at, is_deleted, is_compressed

-- kv_store columns:
-- agent_id, key, value, ttl, expires_at, updated_at   (PK: agent_id, key)

-- session_usage columns:
-- agent_id, cache_hit, cache_miss, output_tokens, model,
-- last_request_hit, last_request_miss, created_at

-- pending_runs columns:
-- agent_id, mode, block_checkpoint, created_at
```

See `BlockRepository.cs` `InitializeAsync()` for full DDL.

## Versioning

The version lives in `version.txt` (read at build/publish time). `dotnet build` in Debug
auto-increments the patch; `./publish.sh` bumps it for a release. There are no git tags — the version
in `version.txt` at each commit is the release marker. Keep `CHANGELOG.md` up to date with every
release.
