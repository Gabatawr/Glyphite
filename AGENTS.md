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

## Session state (Jun 24 — v1.1.23)

### Latest changes — compaction strategies (fibo-parts, struct-cut), ephemeral flag, usage restore on subagent_run

**1. Compaction strategies — два переключаемых режима (6 файлов):**

| Было | Стало |
|:-----|:------|
| Одна стратегия фибоначчи, хардкод | Две стратегии: `fibo-parts` (дефолт) и `struct-cut`, переключение через `Strategies: {"fibo-parts": true, "struct-cut": false}` |
| `Strategy: "fibo-parts"` строка | `Strategies: {"fibo-parts": true, "struct-cut": false}` — словарь флагов |
| Free-form промпт для суммаризации зон | Структурированные шаблоны: **fibo-parts** → Topics/Key Actions/Results/State Changes/Open, **struct-cut** → Goal/Progress/Key Decisions/Relevant Files/Next Steps |
| Весь компакт в одном файле | Разделение: `FiboPartsStrategy.cs`, `StructCutStrategy.cs`, `CompactionService.cs` как фасад |
| Обе включены → ошибка | Random выбор из включённых |

**2. Ephemeral + usage restore для subagent_run (3 файла):**

| Было | Стало |
|:-----|:------|
| `saveMemory` (AdditionalProperties, use) + `isDryRun` (внутренний, run) | Единый флаг `ephemeral` в AdditionalProperties |
| `subagent_run` → `ClearUsageAsync` — удалял ВСЁ usage агента | `subagent_run` → restore usage к checkpoint (как будто run'а не было) |
| `includeMemory` проверял `isSubagent + saveMemory` | `includeMemory = !isEphemeral` |
| Компакт всегда выполнялся, даже для subagent_run | `ephemeral=true` → компакт пропускается |
| `RunAgentTask` возвращал только `(result, blockCk)` | Возвращает `(result, blockCk, ckHit, ckMiss, ckOutput)` |

**3. Файлы изменения:**

| Файл | Изменения |
|---|---|
| `Configuration.cs` | `Strategy` string → `Strategies` `Dictionary<string,bool>` |
| `FiboPartsStrategy.cs` | **Новый** — Fibonacci-зоны, структура Topics/Key Actions/Results/State Changes/Open |
| `StructCutStrategy.cs` | **Новый** — все старые turn'ы → один LLM, структура Goal/Progress/Decisions/Files/Next Steps |
| `CompactionService.cs` | Фасад: диспатч, `PickStrategy()`, общие `SummarizeZoneAsync` + `GroupByTurns` |
| `TurnProcessor.cs` | `compactArgs` со `Strategy`, `ephemeral` → пропуск компакта |
| `SubAgentTool.cs` | `saveMemory`+`isDryRun` → единый `ephemeral`; restore usage к checkpoint |
| `appsettings.json` | `Strategy` → `Strategies` с флагами |
| тесты | +3 validation, +1 config для `Strategies` |

## Session state (Jun 23 — v1.0.16)

### Previous — subagent escape handling, crash-safe pending_runs, agent_task block type, todo match-by-text

**1. Subagent escape — CancellationToken propagation + dry-clean on cancel (6 files):**

| Проблема | Решение |
|:---------|:--------|
| Escape не доходил до суб-агентов — `CancellationToken.None` в `RunAgentTask` | CancellationToken проброшен через всю цепочку: `AIFunction` → `RunAgentTask` → `TurnProcessor.ProcessAsync` |
| `OperationCanceledException` прятался в `catch(Exception)` → возвращал строку ошибки | Отдельный `catch (OperationCanceledException) { throw; }` — пробрасывает наверх |
| При Escape dry-clean блоков не выполнялся (код стоял после `RunAgentTask` в `try`) | Dry-clean перенесён в `catch (OperationCanceledException)` — блоки + usage чистятся перед throw |
| Usage терялся при краше/отмене — писался одной строкой в конце turn | Per-iteration запись через `OnIterationRecorded` callback — каждая итерация пишется в БД сразу |
| После Escape UI показывал устаревшие `_lastTurn*` значения | `LastIteration*` свойства (total/Last hit/miss/output) — ChatRepl читает их при Escape |

**2. Crash-safe `pending_runs` table (3 файла):**

| Проблема | Решение |
|:---------|:--------|
| При краше процесса во время `subagent_run`, `finally` не выполнялся → орфан-агент навсегда в БД | Таблица `pending_runs` в SQLite — запись ДО создания агента, чистка в `finally` |
| При краше между `SetPendingRunAsync` и `CreateAgentAsync` — орфан | `CleanupOrphanRunsAsync()` вызывается в начале каждого `subagent_run/use` — находит pending-записи от crashed сессий |
| GUID-агент после краша | `mode=="run"` + GUID → `DeleteSessionAsync` (полное удаление) |
| Named-агент после краша (dry-run) | `mode=="run-dry"` → `ClearUsageAsync` + `DeleteBlocksSinceAsync(checkpoint)` |

**3. `agent_task` block type (8 файлов):**

| Что изменилось | Детали |
|:---------------|--------|
| Новый `BlockType.agent_task` | В `MemoryBlock.cs` enum + factory `AgentTask(string)` |
| Флаг `isSubagent` | `SubAgentTool.RunAgentTask` ставит `chatOptions.AdditionalProperties["isSubagent"] = "true"` |
| `TurnProcessor.ProcessAsync` | Проверяет флаг — создаёт `AgentTask(input)` вместо `UserMessage(input)` для субагентов |
| `ConsoleRenderer` | `case BlockType.agent_task:` — рендер Cyan `> ...` (отличается от user_message 👤) |
| `BlockTypeIcon` | `["agent_task"] = "📋"` |
| `ProtectedBlockTypes` | `"agent_task"` добавлен — защищён от compaction |

**4. TodoTool — match by text (1 файл):**

| Было | Стало |
|:-----|:------|
| `update` требовал `index` для обновления — LLM не знала точных индексов | Если `text` передан без `index` — ищется существующий item с таким же текстом (case-insensitive). Найден → обновляется. Не найден → добавляется новый |
| `{status:"done"}` без текста — ошибка "missing text" | Если только статус без текста и без индекса — ищет первый item с таким же статусом? Нет — просто обновляет по тексту. Если текста нет и index нет — ошибка как и было |

**Files changed:**
- `src/Glyphite.Abstractions/Models/MemoryBlock.cs` — +`agent_task` enum + factory
- `src/Glyphite.Host/Tools/SubAgentTool.cs` — CancellationToken проброс, dry-clean в catch OCE, CleanupOrphanRunsAsync, isSubagent флаг
- `src/Glyphite.Host/Services/TurnProcessor.cs` — AgentTask для субагентов, LastIteration* свойства
- `src/Glyphite.Host/Services/UsageTracker.cs` — LastOutputTokens
- `src/Glyphite.Host/Services/FailSafeChatClient.cs` — OnIterationRecorded callback
- `src/Glyphite.Abstractions/Interfaces/IAgentStore.cs` — Set/Clear/GetPendingRunsAsync
- `src/Glyphite.Host/Data/SessionRepository.cs` — pending_runs table
- `src/Glyphite.Host/Tools/TodoTool.cs` — match by text
- `src/Glyphite.Cli/ChatRepl.Input.cs` — UpdateFromLastIteration()
- `src/Glyphite.Cli/ChatRepl.Streaming.cs` — вызов UpdateFromLastIteration на Escape
- `src/Glyphite.Cli/Services/ConsoleRenderer.cs` — agent_task рендер
- `src/Glyphite.Host/Utils/BlockTypeIcon.cs` — иконка agent_task
- `src/Glyphite.Cli/appsettings.json` — agent_task в ProtectedBlockTypes

### Previous — Tech debt cleanup, compaction UI fix, TodoTool improvements (v0.7.47–0.7.77)

**1. Batch tech debt fixes (11 items) — 30 files changed:**

| # | Problem | Fix |
|---|---------|-----|
| #1 | Dead code in `TurnProcessor.Streaming` (ContextMessages modifications) | Removed — never reached LLM (FailSafeClient uses own copy) |
| #2 | 75 lines boilerplate duplicated across 3 repositories | Extracted `RepositoryBase` with `WithLockAsync`, `CreateReadConnection`, `Dispose` |
| #3 | Hardcoded `"deepseek-v4-flash"` in SubAgentTool | Uses `_deepseek.Model` from config |
| #4 | SessionManager 850 lines mixing config loading + agent selection + lifecycle | Extracted `ConfigLoader` + `AgentPicker` → 743 lines, clear separation |
| #5 | Duplicated usage parsing in CompactionService + UsageTracker (triple switch) | Shared `UsageParser.Normalize()` + `UsageParser.Parse()` |
| #6 | Icon map (`blockType → emoji`) duplicated in 3 files | `BlockTypeIcon.Get(type)` with `FrozenDictionary` |
| #7 | `Directory.GetCurrentDirectory()` called 5 times | Cached in `_cwd` field |
| #8 | Empty `catch { }` in SearchTools (5 occurrences) | `logger?.LogWarning(...)` with ILogger injected via ToolRegistry |
| #9 | `cfg!` null-forgiving in TodoTool | `IConfigService cfg` non-nullable |
| #10 | Magic string section names in 22 files | All using `*Options.Section` constants |
| #11 | `BlockEntity`/`ConfigRow` in SessionRepository, used by BlockRepository | Moved to `Data/Models.cs` |

**2. Round 2 — cfg!, Task.Run, ReadLineWithHistoryAsync, SessionManager partial:**

| # | Problem | Fix |
|---|---------|-----|
| #12 | `cfg!` in 4 more files (BashTool, WebFetchTool, FileReadTool, SearchTools) | All `IConfigService?` → `IConfigService` |
| #13 | `Task.Run` wrapping async in SubAgentManager | Removed — semaphore already serializes |
| #15 | ReadLineWithHistoryAsync: 207-line switch for 20 keyboard handlers | Extracted each case into named method (HandleEnter, HandleEscape, HandleUpArrow, etc.) — main loop now 42 lines |
| #16 | SessionManager still 743 lines | Split into `SessionManager.Commands.cs` (529 lines commands) + `SessionManager.cs` (221 lines core) |

**3. Compaction UI freeze fix:**

Before: `TryCompactAsync` ran the full LLM summarization **before** yielding to the UI — screen froze for seconds.

After: split into two methods:
- `ShouldCompactAsync()` — fast check (DB, no LLM) — runs first
- `CompactAsync()` — slow LLM summarization — runs **after** `[AutoTool: compression]` notification is yielded to the UI

```csharp
// TurnProcessor now:
var shouldCompact = await _compactionService.ShouldCompactAsync(...); // fast
if (shouldCompact)
{
    yield return new AutoToolTurnEvent("compression", ...); // UI shows immediately
    var compacted = await _compactionService.CompactAsync(...); // slow — user sees progress
}
```

**4. TodoTool improvements:**

- **`list` action** — view a specific list by title or all lists
- **Title as immutable ID** — each `create(title)` creates a fixed-ID list. `update(title)` finds by ID, not the latest. Multiple independent lists possible.
- **Serialization fix** — replaced `Deserialize<object>(Serialize(...))!` with `SerializeToElement(currentItems)` — clean `JsonElement`, no roundtrip
- **Type-safe FormatItems** — `List<Dictionary<string, object?>>` instead of `IEnumerable<object>` with casts
- **Config keys fixed** — `todo_write`/`todo_update` → `todo` in ToolMaxLength/ToolHiddenArgs (they never matched the actual tool name)

### Previous — Session state (Jun 22) — ChatRepl refactoring into SessionManager, MemoryStore split, TurnContext extraction (v0.7.7–0.7.47)

**1. MemoryStore→ 3 repositories — ISP split:**

`IMemoryStore` (composite interface) removed. Three separate interfaces, each with own SQLite connection + semaphore:

| Interface | Implementation | Responsibility |
|-----------|---------------|----------------|
| `IAgentStore` | `SessionRepository` | Agent CRUD, usage stats, launch tracking, fork/delete |
| `IBlockStore` | `BlockRepository` | Block CRUD, atomic `ReplaceBlocksSinceAsync`, load by type/number |
| `IConfigStore` | `ConfigRepository` | Config read/write by session scope |

`MemoryStore.cs` deleted entirely. DI registers three singletons directly.

**2. `_writeLock` → `WithLockAsync` helper:**

Before: ~30 manual `WaitAsync`/`try`/`finally`/`Release` blocks across MemoryStore — copy-paste boilerplate.
After: single `WithLockAsync(Func<Task>)` / `WithLockAsync<T>(Func<Task<T>>)` per repository. Every lock acquisition is one line.

```csharp
// Before
await _writeLock.WaitAsync();
try { /* 20 lines of logic */ }
finally { _writeLock.Release(); }

// After
await WithLockAsync(async () => { /* 20 lines of logic */ });
```

**3. FailSafeChatClient → ToolExecutor + UsageTracker (540→310 lines):**

Extracted two focused classes from the monolithic `FailSafeChatClient`:

| Class | Lines | Responsibility |
|-------|-------|---------------|
| `ToolExecutor` | 202 | Grouping concurrent/sequential tool calls, tracking peek and memory-clean call IDs |
| `UsageTracker` | 92 | Extracting cache tokens from API response, recording to IAgentStore |
| `FailSafeChatClient` (remaining) | 310 | Orchestrator — streaming/non-streaming loops, delegates to ToolExecutor and UsageTracker |

**4. TurnProcessor → TurnContext extraction (400→447 lines):**

Local functions (`ProcessUpdate`, `FlushReasoning`, `FlushText`, `FlushAll`) and helpers (`CleanToolArgs`, `BuildPeekCleanMessage`) moved into `TurnContext` class:

- `TurnProcessor.cs` (43 lines) — pure orchestration: prepare → stream → finish. Three clear phases, `yield return` only in `ProcessAsync`.
- `TurnProcessor.Streaming.cs` (278 lines) — `TurnContext` with full streaming state. Logic unchanged — only code moved.

**5. ChatRepl → SessionManager + InputHistory (cleanup #10):**

Removed 2 partial files (`ChatRepl.Config.cs`, `ChatRepl.Startup.cs`). ChatRepl now has 3 partials + main class:

| File | Lines | Responsibility |
|------|-------|---------------|
| `ChatRepl.cs` | 97 | DI fields + `RunAsync` loop — pure orchestration |
| `ChatRepl.Input.cs` | 465 | Line editor with history, tab-completion, multi-line Redraw |
| `ChatRepl.Commands.cs` | 121 | Command dispatcher, delegates to SessionManager |
| `ChatRepl.Streaming.cs` | 155 | Streaming event loop, RenderChunk + ProcessInputAsync |

New services:

- **`SessionManager`** (850 lines) — owns `_agentId`, `_currentScope`, `SwitchScope()`. Handles startup/resume, all `/команды` (new/clone/use/delete/show models), config loading from disk (Glyphite.json + Glyphite.{agent}.json). Injected as singleton via DI.
- **`InputHistory`** (6 lines) — `List<string>` subclass shared between ChatRepl.Input and InitializeAfterAgentAsync (pre-seeds user messages and commands from DB).

ChatRepl no longer has DI deps on `IAgentManager`, `IAgentScopeFactory` — all agent lifecycle goes through SessionManager.

**6. Input line scroll fix (v0.7.42–v0.7.47):**

Fixed: when `_promptLine` is near the bottom of the terminal buffer and input wraps, `Console.Write(text)` causes a terminal scroll. After scroll, `_promptLine` stayed fixed — subsequent `Redraw` calls wrote over history above.

Fix: after `Console.Write(text)`, compare `Console.CursorTop` (actual last line) with expected `_promptLine + lineCount - 1`. On mismatch, recalculate `_promptLine = Console.CursorTop - (lineCount - 1)`. Without scroll — `_promptLine` stays fixed, no drift.

### Previous — Serilog logging, atomic compaction, parallel summarization (v0.7.0–0.7.6)

**1. Serilog + ILogger\<T\> — structured file logging:**

Replaced all `Console.Error.WriteLine` / `Console.WriteLine` in Host services with `ILogger<T>` or `Serilog.Log`:

```csharp
// Before
catch { Console.Error.WriteLine("[TurnProcessor] Failed to parse args for path"); }

// After
catch { _logger.LogWarning("Failed to parse args for path"); }
```

- **Path:** `~/.glyphite/logs/{dd-MM-yyyy}-{run}.log`
- **Level:** Information+ (turn start/end, compaction, MCP events, tool iterations)
- **Subagent isolation:** subagents write to the same log file but **never to console** — no UI pollution
- `Bootstrapper.cs`: `Log.Logger` configured with `WriteTo.File(...)`, `.UseSerilog()` in Host builder
- NuGet deps: `Serilog 4.2.*`, `Serilog.Sinks.File 6.0.*`, `Serilog.Extensions.Hosting 9.0.*`

Services migrated (15 replacements total):

| Service | Changes |
|---------|---------|
| `TurnProcessor` | 4× `Console.Error` → `_logger.LogWarning`, added `_logger.LogInformation` for turn start/end |
| `CompactionService` | 1× `Console.Error` → `_logger.LogWarning`, added start/end Information logs |
| `McpService` | 6× `Console.Error` + 1× `Console.WriteLine` → `_logger.LogWarning/LogInformation` |
| `FailSafeChatClient` | 1× `Console.Error` → `_logger.LogWarning`, added iteration count log |
| `BashSession` | 2× `Console.Error` → `Serilog.Log.Warning` |
| `ConfigService` | 1× `Console.WriteLine` → `_logger.LogInformation` (`LogAction` preserved) |

**2. Atomic compaction via `ReplaceBlocksSinceAsync`:**

Before: three separate DB calls (`DeleteBlocksSinceAsync` + `RemoveBlocksAsync` + `AppendBlocksAsync`) — crash between them = **data loss**.

After: single `ReplaceBlocksSinceAsync` in one SQLite transaction:

```csharp
await using var tx = await _conn.BeginTransactionAsync();
// 1. Soft-delete individual blocks (old zone unprotected blocks)
// 2. Hard-delete everything from cutoff point
// 3. Insert new blocks (summaries + preserved)
// 4. Update next_number
await tx.CommitAsync();
```

- `IBlockStore.ReplaceBlocksSinceAsync(agentId, fromNumber, newBlocks, nextNumber, softDeleteNums)`
- On crash — SQLite rollback, all data intact. Protected blocks from failed summarization are preserved via `summarizedFallback`.

**3. Parallel zone summarization:**

Old zones (3+) are now summarized **in parallel** via `Task.WhenAll` instead of sequentially:

```csharp
// Fan-out: all zones start simultaneously
var zoneTasks = new List<(List<MemoryBlock> zone, Task<string?> task)>();
foreach (var zone in zoneProtectedBlocks)
    zoneTasks.Add((zone, SummarizeSingleZoneAsync(sessionId, zone, model)));

// Fan-in: collect results
foreach (var (zone, task) in zoneTasks)
{
    var summary = await task;
    ...
}
```

Before: 6 zones = 6× sequential LLM latency (~12s). After: 6 zones ≈ latency of the slowest one (~2s).

**4. Compaction usage tracking from response:**

Before: compaction LLM calls were **invisible** in session usage stats.

After: `SummarizeSingleZoneAsync` parses `response.RawRepresentation` for `Usage.InputTokenCount`, `CachedTokenCount`, `OutputTokenCount` and records directly to session:

```csharp
var response = await _chatClient.GetResponseAsync(messages, chatOpts);
// ... extract Usage from response.RawRepresentation ...
if (hit > 0 || miss > 0 || output > 0)
    await _agentStore.RecordUsageAsync(sessionId, hit, miss, output, model: model);
```

Same pattern as SubAgentTool — real token costs from actual API response.

**5. `peek_clean` → `peek_reasoning`:**

Renamed the auto-tool block for clarity:

| Aspect | Before | After |
|--------|--------|-------|
| auto_tool name | `peek_clean` | `peek_reasoning` |
| LLM description | "peek blocks cleaned from previous turn" | "peek reasoning blocks cleaned from previous turn" |
| Config key | `peek_clean: 0` | `peek_reasoning: 0` |

Impact: `TurnProcessor.cs`, `system-prompt.md`, `Glyphite.json`, `appsettings.json`.

**6. `[AutoTool: compression]` notification:**

When compaction triggers (threshold exceeded), an auto-tool block is shown **before** the LLM summarization call, explaining the delay:

```
[AutoTool: compression | {"AutoCompress":true,"AutoThreshold":75}]
```

Only shown when compaction actually runs — not every turn.

**7. No hardcoded Temperature/MaxOutputTokens:**

```csharp
// Before
var chatOpts = new ChatOptions
{
    ModelId = model,
    Temperature = 0.3f,       // ← hardcoded
    MaxOutputTokens = 512      // ← hardcoded
};

// After
var chatOpts = new ChatOptions
{
    ModelId = model            // ← model defaults only
};
```

Removed from both `CompactionService.SummarizeSingleZoneAsync` and `SubAgentTool.RunAgentTask`.

### Previous — Parallel tool execution & subagent refactor (v0.4.62+)

**1. Parallel tool execution (`FailSafeChatClient.cs`):**

Added `BuildToolGroups` — groups consecutive parallel-safe tool calls into batches for concurrent execution:

```csharp
// Parallel-safe tools:
"read_file", "fetch_web", "search_glob", "search_grep", "subagent_use", "subagent_run"
```

- Tools with `mode="parallel"` and same agent `name` are **split into separate groups** (sequential), preventing race conditions
- Tools with `mode="sequential"` (default) or no mode are executed one at a time
- Parallel batch uses `Task.WhenAll` — all tasks start, results yield as each completes

**2. `SubAgentManager.RunAsync` — SemaphoreSlim added:**

Each `AgentScopeEntry` now has a `SemaphoreSlim(1,1)`. `RunAsync` acquires it before execution and releases after, protecting against concurrent access to the same agent scope.

```csharp
await entry.Semaphore.WaitAsync();
try { return await Task.Run(async () => await runFunc(entry.Scope)); }
finally { entry.Semaphore.Release(); }
```

**3. `subagent_run` — three modes (replaces `saveMemory` logic):**

| Scenario | Behavior |
|---|---|
| no name (auto-GUID) | Creates temp agent → runs → **deletes entirely** |
| name + agent doesn't exist | Creates temp agent with config → runs → **deletes entirely** |
| name + agent exists | **Dry-run**: runs → cleans **only delta** blocks/usage. Existing memory preserved |

Used with `mode="parallel"` for concurrent one-shot tasks.

**4. `subagent_use` — always persistent, always auto-creates:**

- `saveMemory` parameter **removed** — now **always** preserves memory and context
- If agent doesn't exist — **auto-creates** (no need for `saveMemory=true` flag)
- `memory` tool always available for subagents created via `subagent_use`
- Used with `mode="parallel"` to delegate concurrent work to named agents

**5. Tool streaming config (`appsettings.json`):**

Added `subagent_run` and `subagent_use` to `ToolStreaming:ToolMaxLength` (default: `-1` = full output). Can be set to `0` (hidden) or `N` (first N chars).

### Previous — Subagent auto-create & config extraction (v0.4.58–0.4.61)

**Problem:** `subagent_use saveMemory=true` couldn't be used on non-existing agents — there was no tool to create a persistent subagent. `subagent_run` always deleted the agent after execution. Also, config loading logic was mixed into the static `SubAgentTool` class, making it untestable and hard to reuse.

**Changes in `SubAgentTool.cs`:**

1. **Auto-create on `subagent_use saveMemory=true`** (lines 227–245):
   - If agent exists → proceeds as before
   - If agent doesn't exist AND `saveMemory=true` → validates name, calls `CreateAgentAsync`, loads config, then executes
   - If agent doesn't exist AND `saveMemory=false` → returns error with hint to use `saveMemory=true` or `subagent_run`
   - `IAgentManager` and `ISubAgentConfigLoader` injected via DI

2. **Config loading extracted** (lines 80–144 removed):
   - `LoadSubAgentConfigAsync`, `ReadAndFlattenConfigFileAsync`, `FlattenJsonElement` moved to `SessionConfigLoader` service
   - New interface: `ISessionConfigLoader` in `Glyphite.Abstractions`
   - New implementation: `SessionConfigLoader` in `Glyphite.Host.Services`
   - Registered as singleton in DI (`HostServiceCollectionExtensions.cs`)
   - `SubAgentTool.cs` slimmed from 350 to 284 lines (-66 lines)

**Dead code removed:**
- `ToolFactory.cs` (32 lines) — unreferenced legacy factory, deleted

### Previous — Peek & MessageList cleanup (v0.4.40–0.4.48)

**Problem:** `TurnProcessor.ProcessUpdate` was cleaning `contextMessages` (dead code — never read after line 81). The actual LLM-visible data was in `FailSafeChatClient.messageList`, which was **never cleaned**. Peek results accumulated and were visible to LLM on every iteration.

**Fix in `FailSafeChatClient.cs`:**
Two independent cleanup mechanisms, both running **after LLM consumes the result** (after streaming loop, before `hasToolCall` check):

1. **Peek cleanup** (lines 133–149):
   - Tracks `_pendingPeekCallIds` (callId of tools with `peek=true`)
   - After LLM generates response → replaces peek results with `"(peek)"` in `messageList`
   - Keeps `FunctionResultContent` (same callId) so API doesn't reject unmatched tool_calls
   - LLM sees the real result **exactly once**, then sees `"(peek)"` on subsequent iterations

2. **Peek cleanup in messageList** (lines 151–167):
   - After LLM generates response → replaces peek results with `"(peek)"` in `messageList`
   - Works alongside `TurnProcessor` peek handling

**Two independent cleanup paths:**

| Path | What it cleans | When |
|------|---------------|------|
| **TurnProcessor** (DB) | `tool_result` in SQLite (skips saving for peek), deletes `is_deleted=1` | During tool execution (after FunctionResultContent) |
| **FailSafeClient** (messageList) | `ChatRole.Tool` messages with peek results → truncate to `"(peek)"`; deleted block messages → remove from list | After LLM consumes the result (next iteration start) |

**Note:** The `ContextMessages` modifications that were once dead code in `TurnProcessor.Streaming` have been **removed** (cleanup happens in `FailSafeClient` on `messageList`).

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