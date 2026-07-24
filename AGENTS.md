# Agents' workflow

We work from the **published** version of the application — the single-file binary in `~/.glyphite/`.

**I (the agent) build** — I run `dotnet build` and fix any compilation errors.

**You (the user) publish** — when the build is green, you run `./publish.sh` which creates the single-file binary, backs up the previous version, and updates `~/.glyphite/glyphite`.

After publish — exit and restart glyphite to test the new build.

> **Important:** never modify `appsettings.json` (embedded in binary). Changes to it require a rebuild + republish. Use `Glyphite.json` for overrides.

## Configuration hierarchy

Settings are applied in this order (each overrides the previous):

1. **`appsettings.json`** (embedded in the binary) — base defaults. **Do not modify** — it's compiled into the binary.
2. **`Glyphite.json`** in current working directory — overrides base defaults. Hot-reloaded via `IConfiguration` on every turn.
3. **`Glyphite.{agentName}.json`** in current working directory — agent-specific overrides.

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

Instructions are built **every turn** as a single string and set via `ChatOptions.Instructions` (for all agents — CLI and subagent).

**Merge order** (each appended in order):
1. **`system-prompt.md`** (embedded in Glyphite.Host) — always present, cached forever
2. **`AGENTS.md`** (cascade: home → parentCwd → agentCwd, top wins) — only if `Memory:ReadAgentsFile: true`
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
> Example files are in [`agents/`](./agents/) — copy to `Glyphite.{name}.md`.

**Available examples** (inspired by [gstack](https://github.com/garrytan/gstack)):

| File | Role | Focus |
|------|------|-------|
| [`example-qa-agent.md`](./agents/example-qa-agent.md) | QA Engineer | Testing, bug finding, regression verification |
| [`example-review-agent.md`](./agents/example-review-agent.md) | Code Reviewer | PR review, correctness, safety, maintainability |
| [`example-release-agent.md`](./agents/example-release-agent.md) | Release Manager | Version bumps, changelog, publishing |
| [`example-doc-agent.md`](./agents/example-doc-agent.md) | Doc Engineer | README, API docs, architecture docs |
| [`example-arch-agent.md`](./agents/example-arch-agent.md) | Software Architect | Design, ADR, diagrams, migration plans |
| [`example-ceo-agent.md`](./agents/example-ceo-agent.md) | Tech Lead | Strategy, priorities, decision framework |
| [`example-security-agent.md`](./agents/example-security-agent.md) | CSO | OWASP, STRIDE, vulnerability audit |
| [`example-spec-agent.md`](./agents/example-spec-agent.md) | Spec Writer | Requirements, design docs, specs |
| [`example-investigate-agent.md`](./agents/example-investigate-agent.md) | Debugger | Root cause analysis, systematic debugging |
| [`example-refactor-agent.md`](./agents/example-refactor-agent.md) | Refactoring | Code cleanup, safe incremental restructuring |
| [`example-perf-agent.md`](./agents/example-perf-agent.md) | Performance | Profiling, optimization, benchmarking |
| [`example-onboard-agent.md`](./agents/example-onboard-agent.md) | Mentor | Onboarding, codebase tour, learning

## Latest changes — compaction strategies (fibo, struct), ephemeral flag, usage restore on subagent_run

**1. Compaction strategies — two switchable modes (6 files):**

| Before | After |
|:-------|:------|
| Single Fibonacci strategy, hardcoded | Two strategies: `fibo` (default) and `struct`, switched via `Strategies: {"fibo": true, "struct": false}` |
| `Strategy: "fibo"` string | `Strategies: {"fibo": true, "struct": false}` — dictionary of flags |
| Free-form prompt for zone summarization | Structured templates: **fibo** → Topics/Key Actions/Results/State Changes/Open, **struct** → Goal/Progress/Key Decisions/Relevant Files/Next Steps |
| All compaction in one file | Split: `FiboStrategy.cs`, `StructStrategy.cs`, `CompactionService.cs` as facade |
| Both enabled → error | Random selection from enabled ones |

**2. Ephemeral + usage restore for subagent_run (3 files):**

| Before | After |
|:-------|:------|
| `saveMemory` (AdditionalProperties, use) + `isDryRun` (internal, run) | Single `ephemeral` flag in AdditionalProperties |
| `subagent_run` → `ClearUsageAsync` — deleted ALL agent usage | `subagent_run` → restore usage to checkpoint (as if run never happened) |
| `includeMemory` checked `isSubagent + saveMemory` | `includeMemory = !isEphemeral` |
| Compaction always ran, even for subagent_run | `ephemeral=true` → compaction skipped |
| `RunAgentTask` returned only `(result, blockCk)` | Returns `(result, blockCk, ckHit, ckMiss, ckOutput)` |

**3. Files changed:**

| File | Changes |
|------|---------|
| `Configuration.cs` | `Strategy` string → `Strategies` `Dictionary<string,bool>` |
| `FiboStrategy.cs` | **New** — Fibonacci zones, structured Topics/Key Actions/Results/State Changes/Open |
| `StructStrategy.cs` | **New** — all old turns → one LLM, structure Goal/Progress/Decisions/Files/Next Steps |
| `CompactionService.cs` | Facade: dispatch, `PickStrategy()`, shared `SummarizeZoneAsync` + `GroupByTurns` |
| `TurnProcessor.cs` | `compactArgs` with `Strategy`, `ephemeral` → skip compaction |
| `SubAgentTool.cs` | `saveMemory`+`isDryRun` → single `ephemeral`; restore usage to checkpoint |
| `appsettings.json` | `Strategy` → `Strategies` with flags |
| tests | +3 validation, +1 config for `Strategies` |

**4. Unify compaction evaluation, safe zone threshold refinement (5 files):**

| Before | After |
|:-------|:------|
| `TurnProcessor` had separate `ShouldCompactAsync` + `PickStrategy` calls | Single `EvaluateCompactionStatusAsync(isManual)` — `isManual:true` skips `AutoCompress` check for `/compression` command |
| `/compression` duplicated `PickStrategy` + `GroupByTurns` + `ClassifyZones` manually | `/compression` uses `EvaluateCompactionStatusAsync(isManual: true)` — single source of truth |
| `ShouldCompactAsync` (dead code after unification) | Removed — `EvaluateCompactionStatusAsync` handles both paths |
| Prompt coloring only checked `IsThresholdExceeded` → wrong yellow/red when compaction won't run | Also checks `WillCompact`; new **`Magenta`** color for "context large but all zones already compressed" (rare) |
| Safe zone hard mode at `threshold / 2` (50K @ 100K threshold) | `threshold / 4` (25K @ 100K) — catches oversized last-2-turn zones earlier, reducing magenta scenario frequency |
| `goto` in `TurnProcessor` for early exit | Replaced with clean nested `if` |
| `version.txt` | `1.1.97` → `1.2.0` |

### Architecture
- **Abstractions** — interfaces, models, no deps (except `Microsoft.Extensions.AI`)
  - Includes `ISessionConfigLoader`, `IAgentManager`, `IAgentStore`, `IBlockStore`, `IConfigStore`, etc.
- **Host** — service implementations (TurnProcessor, FailSafeChatClient, ToolExecutor, UsageTracker, SessionRepository, BlockRepository, ConfigRepository, BlockMemoryProvider, SessionConfigLoader, SubAgentManager, RepositoryBase), tools (SubAgentTool, TodoTool, ToolRegistry, etc.), utils (UsageParser, BlockTypeIcon, ToolCallHelper), MCP, DI wiring
- **Cli** — UI only (ChatRepl + 4 partials, SessionManager + Commands partial, InputHistory, ConsoleRenderer, AgentPicker). No persistence logic. Config loading unified via `ISessionConfigLoader` (old `ConfigLoader.cs` removed).

### Peek flow
Two levels of peek cleanup, both in `BlockRepository.cs`:

1. **Inter-iteration** (`ClearPeekMarkersAsync(includeReasoning: false)`) — cleans tool/file peek blocks between tool batches (`RemovePeekBlocksAsync` + set `tool_result = NULL`).
2. **Start-of-turn** (`RemovePeekBlocksAsync(includeReasoning: true)`) — cleans ALL peek blocks from DB (safety net before new turn).

Both are separate from the `FailSafeClient` messageList cleanup — DB and in-memory are independent.

### Schema

```sql
-- Tables:
-- sessions, blocks, session_usage, config, pending_runs, agent_launches, schema_version

-- blocks table columns:
-- id, agent_id, number, type, created_at, content, tool_name, data, model,
-- tool_result, updated_at, is_deleted

-- session_usage columns:
-- agent_id, cache_hit, cache_miss, output_tokens, model, last_request_hit, last_request_miss, created_at

-- pending_runs columns:
-- agent_id, mode, block_checkpoint, created_at
```

See `BlockRepository.cs` `InitializeAsync()` for full DDL.

### Version
`Version.txt`: `1.0.22`, published up to v1.0.22

## Latest changes — ToolExecution, McpExecution, ToolStreaming

**1. ToolExecution — per-tool execution settings (peek, timeout, maxSize):**

| File | Changes |
|------|---------|
| `ToolConfigDecorator.cs` | **New** — wraps any `AIFunction` with `extra_cfg` parameter (peek, timeout, maxSize). Injects into JSON schema, applies timeout via `CancellationTokenSource`, trims output to `maxSize` |
| `ToolRegistry.cs` | Reads `ToolExecution` section from config, wraps each builtin tool with `ToolConfigDecorator` |
| `ToolCallHelper.cs` | Added `IsPeekCall(AITool, ...)` — detects peek using `extra_cfg.peek` → flat `peek` → tool-specific default |
| `FailSafeChatClient.cs` | Passes tool to `IsPeekCall` — uses tool-specific peek defaults |
| `appsettings.json` | Added `ToolExecution` array with per-tool entries |
| `Configuration.cs` | Added `ToolExecutionEntry`, `ToolExecutionOptionsEntry` |
| 8 tool files | Removed legacy `peek` parameter (replaced by `ToolExecution` config) |

**2. ToolStreaming — per-tool display control:**

| File | Changes |
|------|---------|
| `Configuration.cs` | `ToolStreamingEntry` with `Tool` + `Options` (MaxSize, HiddenArgs), `GetMaxLength()`, `GetHiddenArgs()` |
| `ConsoleRenderer.cs` | Uses `ToolStreamingEntry[]` instead of old `Dictionary<string, int>` |
| `appsettings.json` | `ToolStreaming` changed from dictionary to array format |

**3. McpExecution — per-server and per-tool MCP settings:**

| File | Changes |
|------|---------|
| `McpService.cs` | `ResolveExecution` now accepts `toolName`, 6-level hierarchical merging (server→tool). Per-tool resolution in `GetToolsAsync`/`ReconnectAsync` |
| `McpServerOptions.cs` | `McpExecutionEntry.Tool` added (nullable). `McpExecutionOptionsEntry` fields made nullable for merge detection |
| `McpPeekToolAdapter.cs` | **Deleted** — replaced by `ToolConfigDecorator` + `PrefixedAIFunction` |

**4. PrefixedAIFunction — MCP tool name prefixing:**

| File | Changes |
|------|---------|
| `McpService.cs` | `PrefixedAIFunction` — wraps MCP `AIFunction` with `{serverName}_` prefix. Wrapping chain: `ToolConfigDecorator(PrefixedAIFunction(innerMcpTool, serverName))` |
| `McpService.cs` | `extra_cfg` stripped before server call (in `ToolConfigDecorator`), prefix stripped before server call (in `PrefixedAIFunction`) |

### Wrapping chain summary

**Builtin tools:**
```
ToolRegistry → ToolConfigDecorator(originalFunc, ToolExecution options)
```

**MCP tools:**
```
McpService.GetToolsAsync → ToolConfigDecorator(PrefixedAIFunction(innerMcpTool, serverName), McpExecution options)
```

Both chains ensure:
- `extra_cfg` parameter injected into JSON schema (LLM sees it)
- `extra_cfg` stripped before actual tool execution (server/builtin never sees it)
- Peek detected via `extra_cfg.peek` → flat `peek` → config default → tool-specific hardcoded default
- Timeout enforced via `CancellationTokenSource`
- Output trimmed to configurable `maxSize`

## Latest changes — InteractiveConfirmation, SafetyChecker, Reasoning auto-compact, protected blocks removal

### 1. Interactive confirmation for dangerous commands

| File | Changes |
|------|---------|
| `InteractiveConfirmation.cs` | **New** — single-line console panel with arrow-key navigation (← →), timer auto-select, KVStore persistence of last choice. Uses `pendingRead ??= Task.Run(Console.ReadKey)` to avoid accumulating abandoned read tasks in the thread pool |
| `BashTool.cs` | After forbidden-command check, checks `CheckRequireCommands` and shows `InteractiveConfirmation` panel. `Stop` blocks command, `Check` runs SafetyChecker |
| `Configuration.cs` | Added `CheckRequireCommands` (string[]) and `CheckRequireCommandsTimeout` (int, seconds) to `BashOptions` |
| `appsettings.json` | Added `CheckRequireCommands` list (50+ dangerous patterns) and `CheckRequireCommandsTimeout: 5` |

**Key design decisions:**
- Single-line `\r` in-place updates — no multi-line positioning issues
- Timer disabled on first arrow press — explicit choice required
- Last choice persisted per-agent in KVStore (`confirmation_last_choice`)
- Subagents skip panel → `Choice.Check` → SafetyChecker
- Non-interactive fallback: if console is redirected, returns `Choice.Check`

### 2. SafetyChecker — LLM-powered safety evaluation

| File | Changes |
|------|---------|
| `SafetyChecker.cs` | **New** — reads conversation context, makes direct LLM call, returns `SafetyVerdict {allow, why}`. Records usage via `UsageParser.Normalize` |
| `ISafetyChecker.cs` | **New** — interface with `CheckAsync(agentId, command, ct)` |
| `SafetyVerdict.cs` | **New** — record with `Allow` (bool) and `Why` (string) |
| `BashTool.cs` | Integrated `ISafetyChecker` in `ExecuteBash` for the `Check` branch |
| `ToolRegistry.cs` | Passes `ISafetyChecker` to `BashTool.AsAIFunction` |
| `HostServiceCollectionExtensions.cs` | Registered `SafetyChecker` as singleton |

**Context loading:** reads blocks from last `turn` marker → last `user_message` → last `agent_task` → full session. LLM evaluates whether the command is safe given the current conversation.

### 3. Reasoning auto-compaction (replaces PeekReasoning)

| File | Changes |
|------|---------|
| `TurnProcessor.Streaming.cs` | `FlushReasoning` now always saves reasoning blocks (no peek marker) |
| `TurnProcessor.cs` | At turn start, `agent_reasoning` blocks > `AutoCompressReasoningMaxSize` chars are LLM-compacted **in parallel**. `BuildPeekCleanMessage` removed |
| `Configuration.cs` | Removed `PeekReasoning` / `PeekToolReasoning` from `AgentOptions`. Added `AutoCompressReasoning` / `AutoCompressReasoningMaxSize` to `CompressionOptions` |
| `appsettings.json`, `Glyphite.json` | Removed `Agent.PeekReasoning` / `Agent.PeekToolReasoning`; added `Compression.AutoCompressReasoning: true` / `AutoCompressReasoningMaxSize: 6000` |

### 4. Protected blocks system removed

| File | Changes |
|------|---------|
| `Configuration.cs` | Removed `ProtectedBlockTypes` field and validation |
| `IBlockStore.cs`, `IBlockMemoryProvider.cs` | Removed `DeleteBlocksAsync` and `DeleteBlocksByFilterAsync` (dead code) |
| `BlockRepository.cs`, `BlockMemoryProvider.cs` | Removed dead methods; `ReplaceBlocksSinceAsync` simplified (no more `softDeleteNums`) |
| `FiboStrategy.cs`, `StructStrategy.cs` | Removed `protectedTypes`, `allUnprotectedNums`/`allOldNums`, `softDeleteNums`, `memOpts` |
| `CompactionService.cs` | Removed `GetProtectedBlockTypes()`, `SubagentToolNames` (no static protected lists needed) |
| Tests | Removed 3 tests for protected blocks; 114 remain |

### 5. MaxSize semantics fix for ToolConfigDecorator

| Behavior | Before | After |
|----------|--------|-------|
| `MaxSize: -1` | `int.MaxValue` (unlimited) ✅ | `int.MaxValue` (unlimited) ✅ |
| `MaxSize: 0` | `int.MaxValue` (unlimited) ❌ | **`0`** (hide from LLM) ✅ |
| `MaxSize: N` | First N chars ✅ | First N chars ✅ |

```csharp
// Before:
_contentMaxSizeDefault = contentMaxSizeDefault > 0 ? contentMaxSizeDefault : int.MaxValue;
// After:
_contentMaxSizeDefault = contentMaxSizeDefault == -1 ? int.MaxValue : contentMaxSizeDefault;
```

### 6. Task.Run leak fix in InteractiveConfirmation

| Aspect | Before | After |
|--------|--------|-------|
| Threads blocked | ∞ (new `Task.Run` per iteration, abandoned on each timer tick) | Max 1 (`pendingRead ??=`) |
| Keystroke stealing | Yes — old read tasks steal keys from the active reader | No — single reusable read task |

### 7. KV store cleanup on agent deletion

Added `DELETE FROM kv_store WHERE agent_id = @sid` to `DeleteSessionAsync` — previously only `config`, `blocks`, `session_usage` tables were cleaned. `kv_store` entries for deleted agents are now removed.

### Current state

- **Version:** `1.3.24`
- **Tests:** 114/114 (removed 3 protected-blocks tests + 2 timeout tests)
- **Build:** 0 errors, 0 warnings