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
  ImageTool, the pocket set — see "Pocket tools: the module boundary" below — etc.), images
  (`ImageLoader`, `ImageFormats`, `ImageAttachmentSink`), utils
  (UsageParser, BlockTypeIcon, ToolCallHelper), MCP, DI wiring
- **Cli** — UI only (ChatRepl + partials, SessionManager + Commands partial, InputHistory,
  ConsoleRenderer, AgentPicker). No persistence logic. Config loading unified via
  `ISessionConfigLoader` (old `ConfigLoader.cs` removed).

### Agent directory vs process directory

`Directory.GetCurrentDirectory()` is never the answer for a path-taking tool. The base directory is
resolved once per turn in `TurnProcessor.PrepareAsync` (`agentCwd`: a subagent's `cwd`, else the
agent's home, else the process directory) and handed to `IToolRegistry.GetBuiltinToolsAsync` as
`baseDir`; `ToolRegistry` bakes that single value into `read_file`, `write_file`, `patch_file`,
`search_glob`, `search_grep` and `view_image`. Adding a new path-taking builtin means taking `dir`
there — never calling `Directory.GetCurrentDirectory()` again.

Bash follows the same rule on its own timeline: a session starts in the agent's
`Bash:DefaultDirectory`, else its home, else the process default, and `BashOptions` are loaded
**per agent** (`GetOptionsAsync<BashOptions>(Section, agentId)`). Reading them globally made every
per-agent bash override invisible — `BashSessionManager` had exactly that bug, so a subagent's shell
opened in the launch directory no matter what its own config said.

Measured before the fix, with four live `subagent_run` calls: config and `AGENTS.md` came from
`cwd` (visible from inside via `kvstore scope=config`), while `bash pwd` and `write_file("probe.txt")`
resolved to the directory glyphite was launched from. The asymmetry is expensive — it looks like the
model ignoring instructions, while the tools were simply pointed elsewhere.

### One `ChatOptions` per session

`ChatOptions` is created once per session and handed to every turn and every tool iteration, so
anything that mutates it must be idempotent. This bit `AgentChatClient`: it pushes
`AdditionalProperties` into the SDK request by wrapping `RawRepresentationFactory`, and wrapping on
every call stacked one more layer on every iteration on the same object — measured at 1000 calls → a
1000-deep chain, with each request re-running all the previous layers. The wrapper is now a named
`PatchOptionsFactory` that recognises its own delegate target, so the chain stays one layer deep and a
foreign factory is still chained exactly once. `ReasoningEffort` has the same shape: it is assigned
into the shared options and "sticks" after being removed from config (`TODO.md`).

### Config: only the session part is cached

`ConfigService` merges two layers: global keys from `IConfiguration` (the files, `reloadOnChange: true`)
and that agent's own overrides (DB `scope = 'session'` rows, or the loader's in-memory overlay when the
agent is away from home). The cache used to hold the **merged** view per agent and was invalidated only
by writes through the service itself, so `AddJsonFile` could reload all it liked — `Bash`, `Search`,
`Todo`, `WebFetch`, `Compression`, `Image`, `ContentDedup` kept the values they first saw. Now:

- the cache holds **only the session part** — the expensive DB round-trip, nothing else;
- the global snapshot is dropped when `IConfiguration.GetReloadToken()` fires, and the token is read
  *before* flattening, so a reload landing mid-walk is not baked into a half-old snapshot;
- `IConfigStore.GetSessionConfigAsync` is a scope filter, not a subtraction of global keys — a session row
  that shadows a global key of the same name is legitimate, and subtracting dropped it;
- two things are resolved per turn instead of remembered: the model (`chatOptions.ModelId` ← the agent's
  DB row ?? `LLM.Model`) and `BlockMemoryProvider`'s `Agent:AgentName` (through `IConfigService`, not an
  `IOptions` snapshot).

The provider client is the exception: endpoint, key and model are captured when the SDK client is built,
so `LazyChatClient` takes a settings key, rebuilds when it changes and disposes the client it replaces.
It stays a process-wide singleton bound to the **file** — an agent's own `LLM:Endpoint` / `LLM:ApiKey` in
session scope does not reach it (`TODO.md`).

## Peek flow

Two cleanups at two different levels — the request list and the database:

1. **After each iteration** `FailSafeChatClient` calls `ToolExecutor.CleanupPeekTools(messageList)`:
   every tool response whose call id was marked peek is rewritten to `(peek)` in the request, so the
   model sees the result exactly once and the next iteration does not re-send it.
2. **End of turn** `TurnProcessor` calls `ClearPeekMarkersAsync(agentId, includeReasoning: false)`:
   peek blocks lose `$.peek` and their `tool_result` (`type != 'agent_reasoning'`), and the in-memory
   `StoredBlocks` are wiped the same way so the turn marker measures what the history actually keeps.

`$.peek` is only ever written on tool-call blocks (`TurnProcessor.Streaming.cs`), never on reasoning,
so there is nothing to clean for reasoning — which is why the old broad "remove all peek blocks" API
was dead and has been deleted rather than wired up.

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

### Tool results are bounded before they enter the history

`maxSize` (`ToolExecution:*.MaxSize`, default `ToolExecutionDefaults.ContentMaxSize` = 100 000 chars) is
enforced by `ToolConfigDecorator.InvokeCoreAsync`: `-1` = unlimited, `0` = hide, `N` = 1/3 head +
notice + 2/3 tail, with the full output written to `<tmp>/<agent>/<tool>_<ts>.out`.

The notice is part of the budget, not an addition to it: the returned view never exceeds `maxSize`.
It used to be `maxSize` chars of content *plus* ~120 chars of notice — for a small limit that is a
multiple of the limit, and the whole point of the value is to cap what a re-sent tool result occupies.

The trap that made this dead configuration: `AIFunctionFactory` returns a **`JsonElement`**, not a
`string`, so a `result is string` test matched no tool at all. `ToolConfigDecorator.ResultText`
normalizes the string kind first — keep it that way, and never test the raw `object?` for `string`.

`fetch_web` bounds its own output as well (`WebFetch:MaxContentChars`, `WebFetch:MaxDownloadBytes`) and
drops `<script>`/`<style>` bodies, because a docs page is mostly JavaScript: the Anthropic
prompt-caching page measured **2 613 325 chars** (nearly all of it script) before and **64 712 chars of
real text** after.

Why it matters more than it looks: a tool result is rendered into every later request. One unbounded
page pushed a session past the model's 1 M window, and every turn then failed with
`invalid_request_error` until the block was removed — truncation is not cosmetic, it is what keeps a
session alive.

### Stored tool args: what the model asked for, not how it was run

A tool-call block keeps the model's arguments minus two kinds of noise, in `TurnContext.BuildStoredArgs`:

- **bulk payload** that already lives in the result — `content` (`write_file`) and `new`/`old`
  (`patch_file`);
- **`extra_cfg` fields that merely restate the tool's own defaults** — `ToolConfigDecorator.OverridesFrom`
  compares each field against that tool's `peek`/`timeout`/`maxSize` defaults. `peek: true` on a tool that
  peeks by default is not recorded; `peek: false` or `timeout: 5` is, because "ran with a 5s timeout" is
  part of how the call was made, while "used the default" says nothing.

`extra_cfg` is execution plumbing, not an argument: the decorator strips it before the tool is invoked, so
history should not hand it back as if the tool had been asked for it. When no tool instance is known (a
tool that vanished mid-turn), the argument is kept as passed — showing plumbing beats guessing wrongly
about defaults.

### Pocket tools: the module boundary

The pocket subsystem was one 915-line static class with ~20 responsibilities; it is now five pieces,
and the split lines are the seams worth keeping:

| File | Holds | May use |
|---|---|---|
| `PocketEntry.cs` | `PocketArg`/`PocketEntry`, naming rules (`_pocket` suffix, name/category validation) | — |
| `PocketStore.cs` | vault keys, scopes incl. the `__global__` sentinel, load/save/delete, the stored JSON | `PocketEntry` |
| `PocketSchema.cs` | arg JSON in/out, `{arg}` substitution, shell quoting, model JSON schema, entry cards | `PocketEntry`, `PocketStore.JsonOpts` |
| `PocketExecution.cs` | the runner and the materialized native `AIFunction` | `PocketSchema`, `BashTool` |
| `PocketCommands.cs` + `.Edit.cs` | the five `pocket_*` tools, handlers, add-time pre-flight | all of the above |

Two invariants keep it from collapsing back: **only `PocketStore` touches `IKVStore`** (so the stored
format has one home and `SaveAsync` can never disagree with `KeyFor`), and **only `PocketExecution`
touches bash**. Substitution and quoting are `internal` and tested directly — that is where injection
bugs live, and through bash they were unreachable.

## Command safety

Every shell command passes `BashTool.GateAsync` — foreground, `back=true`, and pocket runs alike. Three
invariants, each of which used to be violated:

1. **Match the command, not the first word.** `CommandAnalyzer` splits on `;` `&&` `||` `|` `&`
   newlines and parentheses, expands `$(…)` and backticks, drops leading `FOO=bar`, peels wrappers
   (`sudo`, `env`, `nohup`, `timeout`, `xargs`, `nice`, `stdbuf`, …) and recurses into `bash -c "…"`.
   The old check compared the whole string against `pattern + " "`, so `cd /tmp && sudo reboot` and
   `bash -c "rm -rf /"` were invisible to it.
2. **Anchor at segment start, never substring.** `echo "rm -rf"` and `git log --grep "rm -rf"` mention
   a pattern without running it; hard-blocking those teaches everyone to route around the checker.
3. **One question per command.** However many patterns matched, the human is asked once, about the
   whole string. `skipInteractiveConfirm` (a pre-approved pocket template) is *not* inherited when
   values were substituted — the approved template and the substituted command are different commands.

`SafetyChecker` returns `Inconclusive` instead of `Allow` when it cannot reach a verdict (LLM error,
empty answer, missing `allow`, unparseable JSON). The human then decides with the timer off: a broken
checker is never an approval, and silence is never consent.

### Directories are paths, not strings

`workdir` is checked by `PathGuard.BlockReason` for `bash`, `bash_back` and `pocket_add` — one
implementation, because two copies of this rule drifted into two different bugs at the same time:

- `"/"` was `TrimEnd('/')`-ed to `""`, and every absolute path starts with `"/"`, so the shipped
  default (`"/"`, `"/etc"`, `"/boot"`, `"/sys"`, `"/proc"`, `"/dev"`) rejected **every** absolute
  working directory. The project the agent was told to work in got "Directory forbidden".
- The same string comparison let `../etc` through while `"/etc"` was blocked.

The rules that replace it: both sides go through `Path.GetFullPath` (so `..` and `.` are resolved
before comparing), a rule matches on a segment boundary (`/etc` covers `/etc/passwd`, not `/etcetera`),
a rule of `/` covers only the root itself, and a relative `workdir` is resolved from the directory the
shell will actually start in — `IBashSessionManager.GetStartDirectoryAsync` (per-agent
`Bash:DefaultDirectory` → agent home → process directory), because that is what `cd` inside the shell
resolves against. `pocket_add` has no session yet, so it uses `Bash:DefaultDirectory` or the process
directory. Case sensitivity follows the filesystem: `OrdinalIgnoreCase` on Windows, `Ordinal`
elsewhere.

### Confirmation timeout: the remembered choice is the default

`InteractiveConfirmation` offers three choices in a fixed order — `enum Choice { Ok, Stop, Check }`,
labels `OK · Stop · Check` — and starts the highlight on the agent's last answer
(`confirmation_last_choice` in KVStore). With nothing remembered (first prompt, no KVStore, or an
unreadable value) the highlight starts on **`Check`**: `LoadLastChoiceAsync` returns index `2`. An
untouched timer therefore does not approve the command — `Check` goes to the AI safety check, and only
an explicit `OK` approves. A subagent or a redirected console never sees the prompt at all:
`ShowAsync` returns `Check` immediately.

The timeout returns *and saves* whatever is highlighted, which is the point: the human picks a policy
once and the choice is the reusable part, so changing it changes the starter for the next prompt. The
known cost runs the other way — if a human once pressed `Enter` on `OK`, that `OK` becomes the starter
next time and a timer expiry then approves with nobody reading. Arrow keys switch the timer off, so a
human who starts navigating cannot be auto-selected; a human who walks away after having once chosen
`OK` can be.

The AI path may not lean on any of this: when the checker cannot decide, the prompt is raised with
`noAutoSelect: true` and waits for a real keystroke, so neither the timer nor a remembered `OK` can
approve a command nobody judged.

## REPL input area

The prompt line (`> ` plus the usage panel) is drawn and redrawn without ever asking the terminal
where the cursor is. That is not a style preference:

- **`Console.CursorTop` is a blocking stdin read on Unix.** It writes a Device Status Report
  (`ESC[6n`) and then waits for the terminal's reply from stdin, with no timeout. The failure modes
  are exactly the two that used to be reported: the reply never arrives → the app hangs with the
  last answer on screen and no prompt; the reply arrives but is consumed late → its own bytes are
  then read as keystrokes (`ESC` clears the line, `[41;1R` is typed into it).
- **We know the geometry without asking.** The input area is the last thing on screen and we wrote
  every row of it, so it is redrawn with relative moves only — `ESC[<n>A` / `ESC[<n>B` to reach a
  row, `\r` + `ESC[<n>C` for a column, `ESC[J` (erase below) to drop the tail of a previously longer
  input. `_promptRows` / `_cursorRow` count the rows currently used. Scrolling needs no correction
  either: a scroll shifts both the content and the cursor by the same amount, so relative math holds.
- **An empty line is a no-op.** `HandleCommandAsync` must not index a `Split` result that came from
  `""` — `Split(…, RemoveEmptyEntries)` returns an empty array, and `parts[0]` used to crash the REPL
  on a plain Enter.
- **The answer's tail is flushed when generation ends**, not when the turn ends: `UsageTurnEvent`
  (the first finalize event) flushes the buffered partial line, so the last line of the answer and the
  prompt no longer wait for `FlushAll`, peek cleanup and the turn marker.

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
-- agent_id, cache_hit, cache_miss, output_tokens, reasoning_tokens, model,
-- last_request_hit, last_request_miss, created_at

-- pending_runs columns:
-- agent_id, mode, block_checkpoint, created_at
```

See `BlockRepository.cs` `InitializeAsync()` for full DDL.

### Block numbering

`blocks.number` is the transcript's ordering key. It is assigned in **exactly one place** —
`BlockRepository.AppendBlocksAsync` — inside the write lock and transaction, as
`max(sessions.next_number, MAX(number)+1)`. A stale counter (after a fork, after history was
replaced) therefore heals instead of handing the same number out twice, and soft-deleted rows still
count as taken.

Nothing else may assign `Number` to a block it appends: tools append and read `block.Number` back.
Compaction is the only writer that deliberately renumbers history, through
`ReplaceBlocksSinceAsync`.

### Usage accounting

One API response answers two unrelated questions, and the numbers are never interchangeable:

| number | what it is | where it may be used |
|---|---|---|
| `last_request_hit + last_request_miss` | the prompt **as it was sent**, from the last iteration | context size, compaction threshold, prompt counter |
| `hit`, `miss` | cache split of the prompt; inside a turn marker they are **summed over every iteration** of that turn | cost ($) |
| `out_` (completion) | what the provider **billed** | cost ($) only |
| `reasoning` | hidden thinking, **already inside** `out_` | explaining the bill; never added to anything |

Providers bill reasoning tokens as output, so `$ = miss×Miss + hit×Hit + out_×Output` is already
correct with `out_` as reported — nothing needs to be added or subtracted. The trap is the other
direction: `out_` says nothing about how much text a turn added to the history. Reasoning is partly
hidden from us and partly compressed away, so the stored blocks are always smaller than the billed
output (measured: **2.8x** across a real base — 1.9M billed output vs 684K tokens of text in
blocks). Treating `out_` as "context weight" is exactly what once made `hard` mode fire at half the
intended size; context must come from the rendered blocks (`EstimateZoneTokens`) or from the prompt
count.

The second trap is subtler: a turn runs several iterations, and each one re-sends the whole
conversation. So in a turn marker `hit`/`miss` add up to roughly *N × context* (a 4-iteration turn
bills ~4x its conversation) — billing correctness, but nothing to do with how big the context is.
Only `last_request_*` and the marker's `context` field answer that.

`UsageParser.Parse` returns all four numbers, and the split is persisted in
`session_usage.reasoning_tokens` (schema v2).

Turn markers carry **all three rulers**, so they can never be mixed up later:

```json
{"hit": 41000, "miss": 1200, "out_": 60, "reasoning": 45,
 "context": 10600, "stored": 110, "stored_reasoning": 95,
 "stored_text": 15, "stored_tool": 0}
```

- billing — `hit`/`miss`/`out_`/`reasoning`: what the turn cost, summed over its iterations.
- context — `context` (= `LastHit + LastMiss` of the last iteration): how big the conversation
  actually was when the last request went out. Same quantity as `session_usage.last_request_*`, so
  a marker and the compaction threshold are directly comparable.
- footprint — `stored`: the rendered size of the blocks the model wrote during that turn, split into
  `stored_reasoning` (thinking), `stored_text` (answer text) and `stored_tool` (tool calls: arguments
  plus the result as stored). Measured with the same `EstimateZoneTokens` ruler compaction uses, so a
  reasoning block's size is never confused with an answer's, and neither is ever confused with the
  bill. The three parts add up to `stored` up to per-part rounding (each is floored, so a token or
  two can drift). `stored` is a snapshot taken when the marker is written: later auto-compression of
  reasoning rewrites those blocks, and the marker keeps the original number.

Summary markers follow the same shape: `hit`/`miss` are the summarizer's real prompt, `out_` its billed
output (reasoning inside), `stored` the size of the summary itself — all of it `stored_text`, because a
summary is pure result text. They carry no `context`: a summary is written at the start of a turn,
before the request is built, so the conversation size is not known yet; their `miss` is the
summarizer's prompt, which is roughly the size of the zone it replaced (`stored / miss` is the
compression ratio). The one compaction path without a marker is auto-compression of reasoning
blocks: it rewrites the block in place, so its stored size *is* the block's content, and its billed
usage goes to `session_usage` like any other call.

Rows written before the upgrade report `reasoning = 0` — historical reasoning was never recorded and
cannot be recovered, so the split accumulates from the upgrade on.

### Compaction

Auto-compaction runs at the start of a turn, before the current user message is stored, so only
blocks older than the current turn are in scope. History is grouped into turns (the `agent_data`
header is always its own group), the two newest turns are kept verbatim, and everything older is
replaced by LLM summaries through `ReplaceBlocksSinceAsync` — the raw turns are hard-deleted, so
summary quality *is* history quality. `fibo` (default) summarizes in Fibonacci-sized zones
(`1, 1, 2, 3, 5…` turns, `O(log n)` calls); `struct` (off by default) sends the whole history in a
single structured call and is all-or-nothing on failure. Both escalate to `hard` mode when
already-compressed history outweighs 2/3 of the threshold.

The auto path runs every turn, so it short-circuits: while a recorded usage figure exists and the last
request fits under the threshold, it returns before reading the block table or classifying zones. A
forked session has no `session_usage` rows (a fork copies blocks, not usage), so with nothing to
compare the threshold falls back to `EstimateZoneTokens(blocks)` — a deliberate lower bound, since a
real request also carries the system prompt and tool schemas; it errs towards not compacting rather
than compacting on a guess.

### Reasoning in the context (deliberate)

`agent_reasoning` blocks **are** sent to the provider (`BuildContextAsync` filters only `turn` and
`auto_tool`). That is a decision, not an oversight: the forks, rejected options and "why not" live in
the reasoning, and an answer-only history keeps *what* while losing *why*. It is safe because the
transcript travels as plain text inside `system` messages — not as the provider's structured
`reasoning_content` / signed-thinking field, which is the thing APIs refuse on input.

The only brake on their size is `AutoCompressReasoning` (`Compression:AutoCompressReasoningMaxSize`,
8000 chars), and it skips the **newest** turn: that reasoning is the working memory the next step is
planned on, so rewriting it would edit the very context the request was built from. The boundary is
the last `user_message`/`agent_task` (`TurnProcessor.GetLastTurnStartNumberAsync`); a session with
neither has no boundary, and everything stays eligible (the pre-rule behaviour). Note the newest
*turn* here, while zone compaction keeps the newest **two** — different mechanisms: compaction swaps
whole turns and their tool results, reasoning compression edits one block in place.

## Versioning

The version lives in `version.txt` (read at build/publish time). `dotnet build` in Debug
auto-increments the patch; `./publish.sh` bumps it for a release. There are no git tags — the version
in `version.txt` at each commit is the release marker. Keep `CHANGELOG.md` up to date with every
release.
