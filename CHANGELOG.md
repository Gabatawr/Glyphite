# Changelog

All notable changes to Glyphite are documented here, newest first. Versions come from
`version.txt`, bumped by `./publish.sh`.

## [1.4.75] — 2026-11-03

Covers 1.4.5–1.4.75 (patch releases since the 1.4.0 entry).

### Added

- **Per-agent working directory** — `subagent_run` / `subagent_use` `cwd` is the agent's home *and*
  the base directory of its path-taking tools (`read_file`, `write_file`, `patch_file`, `search_glob`,
  `search_grep`, `view_image`). A per-agent `Bash:DefaultDirectory` / `ExecutablePath` is honoured for
  foreground and background commands alike
- `CommandAnalyzer` — the safety gate reads the command that actually runs: separators (`;`, `&&`,
  `||`, `|`, `&`, newlines, parentheses), `$(…)` / backticks, `FOO=bar` prefixes, wrappers (`sudo`,
  `env`, `nohup`, `timeout`, `xargs`, …) and recursion into `bash -c "…"`. Patterns anchor at segment
  start, and however many patterns match, the human is asked once about the whole string
- `PathGuard` — one directory check shared by `bash`, `bash_back` and `pocket_add`
- `SafetyVerdict.Inconclusive` — the AI checker no longer degrades to `Allow`; the human decides with
  the timer off (`noAutoSelect`), and only an explicit `OK` approves
- Turn markers carry three separate rulers: `context` (the last request's prompt), `stored` /
  `stored_reasoning` / `stored_text` / `stored_tool` (rendered size of what the turn wrote) and billed
  `hit` / `miss` / `out_` / `reasoning`

### Fixed

- **Block numbers came from five independent writers** — a todo block could share a number with a bash
  block. `AppendBlocksAsync` now allocates inside the write lock and transaction from
  `max(sessions.next_number, MAX(number)+1)`; a stale counter self-heals, soft-deleted rows count as
  taken, and `ForkSessionAsync` syncs the counter and `is_compressed`
- **Compaction could delete the oldest turn without a summary** — the first turn was folded into the
  `agent_data` group and `ClassifyZones` skipped it. `agent_data` is always its own leading group,
  zones are ranked from the newest, and a trailing turn without a marker is kept
- **A forked session never compacted** — a fork copies blocks but not `session_usage`, so the
  threshold compared against zero; it now falls back to an estimate of the history
- **`ForbiddenDirectories: ["/"]` blocked every absolute `workdir`** while `../etc` passed:
  `"/".TrimEnd('/')` became `""`. Paths are canonicalized, matched on a segment boundary, and a
  relative `workdir` resolves from the directory the shell really starts in
- **A subagent's `cwd` reached its config but not its tools** — bash opened in the launch directory and
  `write_file("x.txt")` wrote there; `write_file`'s re-read also used the model's raw argument, so it
  could show a same-named file from another directory
- **`extra_cfg` leaked into history** — fields that merely restate a tool's own defaults are dropped;
  a real override (`peek:false`, `timeout:5`) is kept
- **`fetch_web` was unbounded** — one docs page (2.6M chars, nearly all script) pushed a session past
  the context window; the download and the text are bounded separately and `<script>` / `<style>`
  bodies dropped
- **`pocket_run` inherited "approved" after substitution** — a pre-approved template with a
  substituted argument no longer skips the interactive confirmation
- **`AgentChatClient` stacked a new `RawRepresentationFactory` layer on every request** —
  `ChatOptions` lives for the whole session, so the wrapping chain grew 1:1 with calls and every
  request re-ran all the earlier layers (measured: 1000 calls → a 1000-deep chain). The wrapper is a
  named factory that recognises its own delegate target now, so the chain stays one layer deep; a
  foreign factory is still chained exactly once
- **`maxSize` was a limit only approximately** — the truncation notice was appended on top of the full
  budget (~120 chars), so a small limit could be exceeded several times over. The notice now comes out
  of the budget: the returned view never exceeds `maxSize`, and `-1` is handled as "unlimited" instead
  of reaching the splitter
- **Reasoning effort outlived its setting** — it was applied to the session-wide `ChatOptions` and
  never cleared, so removing `LLM:ReasoningEffort` from the config kept the old effort in force; the
  key is now authoritative on every turn
- **Reasoning auto-compaction no longer rewrites the newest turn's reasoning**
- **Edits to `Glyphite.json` never reached a per-agent reader** — `ConfigService` cached the *merged*
  view (file values included) per agent and invalidated it only on writes through itself, so the reload
  token that `AddJsonFile` fires changed nothing: `Bash`, `Search`, `Todo`, `WebFetch`, `Compression`,
  `Image` and `ContentDedup` kept the values they first saw until a restart. The cache now holds only the
  session part; the file is merged fresh every read, with the flattened snapshot dropped when the reload
  token fires
- **Picking a model with `/models` did nothing until `/new`** — the pick is written to the agent's row,
  but the model had been copied into the session-long `ChatOptions` at startup and won over the config.
  It is resolved from the agent row (falling back to `LLM.Model`) at the start of every turn now
- **The provider client kept the endpoint, key and model it was built with** — `LazyChatClient` takes a
  settings key and rebuilds the client when it changes, disposing the one it replaces, so editing
  `LLM.Endpoint` / `LLM.ApiKey` / `LLM.Model` applies without a restart
- **`Agent.AgentName` waited for `/new`** — `BlockMemoryProvider` read it from an `IOptions` snapshot
  taken at scope creation; it goes through `IConfigService` on every context build now
- **Session config cost two queries and could lose a key** — `ConfigService` and `SessionConfigLoader`
  merged the agent's config with the global one and subtracted, which cost two round-trips and dropped
  any session row that shared a name with a global key. `IConfigStore.GetSessionConfigAsync` filters by
  scope instead
- `out_` already contains `reasoning` and says nothing about history size; `/stats` shows the split
  instead of summing them
- **The REPL asked the terminal where its cursor was** — on Unix `Console.CursorTop` is not an ioctl:
  it writes a Device Status Report (`ESC[6n`) and then blocks reading the reply from stdin. Lose that
  reply and the app sits with the answer on screen and no prompt; read it late and its own bytes turn
  up as typed text (`[41;1R` after `> `). The input area is redrawn with relative cursor moves now and
  the rows it occupies are counted from what we wrote — the terminal is never queried
- **The last line of an answer waited for the whole turn to finish** — streaming buffers per line, so
  a trailing line without a newline was flushed only after `FlushAll`, peek cleanup and the turn
  marker. It is flushed when generation ends (`UsageTurnEvent`), so neither the answer's tail nor the
  prompt waits on DB work
- **Enter on an empty prompt killed the REPL** — `input.Split(…, RemoveEmptyEntries)` returns an empty
  array for `""`, and the next line indexed `parts[0]`. Empty and whitespace-only lines are a no-op now

### Changed

- `IToolRegistry.GetBuiltinToolsAsync(..., baseDir)` and `IBashSessionManager.GetStartDirectoryAsync`
  are new; `IBashSessionManager.StartBackgroundAsync` is now async and no longer uses the startup
  snapshot of `BashOptions`
- `Compression:AutoCompressReasoningMaxSize` default 6000 → 8000

## [1.4.0] — 2026-10-01

Covers 1.4.0–1.4.4 (published as patch bumps) together with the 1.3.25–1.3.99 development line.

### Added

- **Image support** — `view_image` tool plus automatic attachment of image paths/URLs found in a user
  message. New `Image` config section (`ImageOptions`): `Enabled`, `AutoAttach`, `UrlMode`,
  `UrlMatching`, `Detail`, `MaxImageBytes`, `MaxTotalBytes`, `MaxImagesPerRequest`, `MaxUrlLength`,
  `MaxDimension`, `DownloadTimeoutSeconds`, `Extensions`
- Container detected from magic bytes (JPEG / PNG / GIF / WebP) and dimensions read from container
  headers — the file name is never trusted
- Provider limits (per-image size, total inline size, image count, URL length, per-side dimension)
  are enforced before the request is built
- `ImageAttachedTurnEvent` — UI notice when an image joins the conversation
- `ImageLoader`, `ImageAttachmentSink`, `ImageFormats`, `ImageTool` in `Glyphite.Host/Images` +
  `Glyphite.Host/Tools`
- `read_file` and `fetch_web` refuse an image with a pointer to `view_image` — a picture decoded as
  text is only junk. `fetch_web` sniffs the body before trusting the declared `Content-Type`; SVG
  stays a document
- `ImageFormats.DescribeImageFile` — bounded-header description (media type, pixel size, byte size)
  used by those refusals
- **Pocket tools** — user-defined tool aliases in the agent vault: `pocket_list`, `pocket_add`,
  `pocket_set`, `pocket_remove`, `pocket_run`. Bash command templates with typed argument schemas;
  `materialize=true` exposes an entry as a native `<name>_pocket` tool; `local`/`global` scope with
  shadowing; safety preflight at add time
- **Global config** — first launch creates `~/.glyphite/Glyphite.json` (a one-time snapshot), so the
  API key is entered once and works from any directory. First-run flow offers: enter the key / create
  a local config (key prompted and written) / skip; the per-folder choice is remembered in
  `state.json`. Non-interactive runs use the global config silently
- **Headless mode** — `subagent_run` / `subagent_use` usable without a TTY
- `read_file`: git status markers (`+` on lines that differ from HEAD) plus a `git_delete` flag that
  appends the deleted lines from the diff
- `search_grep`: `around` — N context lines around each match
- `patch_file`: error output shows the approximate match location instead of the first 20 lines
- Git Discipline section in the system prompt (never commit/push without an explicit user request)
- DeepSeek pricing updated (flash / pro)

### Fixed

- `UrlMode` is matched case-insensitively and reduced to two modes: `auto` (default) is adaptive — a
  public URL is handed to the provider to download, a host the provider can never reach is inlined;
  `inline` always downloads. The `passthrough` mode was removed: on hosts the provider cannot reach it
  was guaranteed to fail the whole request, and on public hosts it was indistinguishable from `auto`.
  Trade-off kept with `auto`: a URL the provider cannot fetch (404, auth, hotlink protection) fails the
  **whole request** with an opaque `400` — use `inline` to spend bandwidth and avoid that
- `view_image` respects the per-request image budget (`Image:MaxImagesPerRequest`,
  `Image:MaxTotalBytes`), shared end to end with images auto-attached to the user message. An
  over-budget image comes back as a tool-level error instead of the provider rejecting the whole request
- The same picture is never attached twice in one turn. An image is keyed by its resolved path, its
  URL, or a content hash for `data:` URLs, and that identity is shared between the pictures
  auto-attached to the user's own message and the ones a tool pulls with `view_image` — a repeated
  request answers "already attached" and sends nothing. Auto-attached images now also count toward
  `Image:MaxImagesPerRequest`, which previously capped only the tool path
- Non-base64 `data:` URLs decode percent-escapes as raw octets — `%E2%82%AC` stays three bytes
  instead of collapsing into one character and corrupting the image
- Console output is flushed at stream end — fixes a hang under Windows buffering
- `SQLITE_BUSY` retry around the per-agent SQLite write path
- `ToolStreaming`: first matching rule wins (was last-wins)
- `TodoTool`: status validation
- Brittle spots found in code review hardened (guard clauses, error paths)

### Changed

- Images loaded by tools are injected as a **user** message after the tool batch (`FailSafeChatClient`
  + `ImageAttachmentSink`) — the API rejects images in tool/assistant/system roles
- User message blocks store a text marker instead of base64, keeping history small and letting the
  agent re-open the image later with `view_image`
- `ImagePayload` carries an explicit `ImagePayloadKind` (`Inline` = bytes in the body, `Passthrough` =
  URL handed over) instead of a bare bool; the shared download client decompresses
  gzip/deflate/brotli so an encoded image still sniffs as one
- `TurnProcessor` split into phases (`TurnProcessor.Phases.cs`, `TurnProcessor.Streaming.cs`);
  `TurnContext` extracted
- Defaults hardened
- `+116` unit tests for critical-path services; CI gained a test step

## [1.3.24] — 2026-07-24

### Added

- **Interactive confirmation** for dangerous bash commands — panel `[OK] [Stop] [Check]` with arrow
  keys, timer, and KVStore last-choice persistence
- **SafetyChecker** — LLM-powered safety evaluation for shell commands (subagent auto-check and
  "Check" panel option). Reads conversation context, returns `{allow, why}`, records usage
- **Reasoning auto-compaction** — large `agent_reasoning` blocks >6K chars are automatically
  LLM-compacted after each turn (replaces old PeekReasoning system)
- `CheckRequireCommands` and `CheckRequireCommandsTimeout` in `BashOptions` — 50+ command patterns
  for dangerous operations
- `AutoCompressReasoning` / `AutoCompressReasoningMaxSize` in `CompressionOptions`
- `kv_store` cleanup on agent deletion (`DELETE FROM kv_store` added to `DeleteSessionAsync`)

### Fixed

- `MaxSize: 0` now correctly hides output from LLM (was being treated as "unlimited")
- `ToolStreaming` config binding — direct array loading instead of `GetOptionsAsync<T>` (which
  mismatched flat config keys with `Entries` property)
- `InteractiveConfirmation` Task.Run leak — `pendingRead ??=` ensures at most 1 blocked thread in pool
  (was accumulating infinitely)
- Wildcard `*` support in `GetHiddenArgs` — mirrors existing `GetMaxLength` logic

### Changed

- `ToolExecution.MaxSize` semantics: `0` = hide, `-1` = unlimited, `N > 0` = first N chars.
  Truncation shows 1/3 top + notice + 2/3 bottom, full output saved to temp file
- Truncation logic centralized in `ToolConfigDecorator` — all tools (builtin + MCP) use the same
  1/3+2/3 + temp file pattern
- Removed protected blocks system (dead code after compaction change — all blocks now go to LLM, all
  are hard-deleted)
- Removed dead fields: `DefaultTimeoutMs` (Bash), `TimeoutSeconds` (WebFetch), `MaxContentLength`
  (WebFetch), `MaxOutput` (Bash), `MaxReadChars` (Search) — replaced by
  `ToolExecution.Timeout`/`MaxSize`
- `read_file` returns hard error when file exceeds `ToolExecution.MaxSize` (no temp file written)
- Removed `PeekReasoning` / `PeekToolReasoning` from config — replaced by `AutoCompressReasoning`
- `appsettings.json` updated: Cyrillic replaced with English labels (OK/Stop/Check), new
  Compression/AutoCompress settings, new Bash/CheckRequireCommands

## [1.1] – [1.3.23] — collapsed

Development between 1.0.0 and 1.3.24, not itemised here. Headlines:

- `kvstore` tool (vault/config scopes, glob masks, TTL, dry-run confirm flow) and `bash_back`
  background tasks; `execute_bash` renamed to `bash`
- `ToolMaxLength` replaced by **ToolStreaming** (console display) and **ToolExecution** (server-side
  truncation, per-tool peek/timeout/maxSize)
- Auto-compaction grew from one hardcoded strategy into two selectable ones — `fibo` (Fibonacci zones)
  and `struct` (single structured summary) — behind `Compression.Strategies`
- Subagent overhaul: `subagent_run` (ephemeral) / `subagent_use` (persistent), `mode="parallel"`,
  graceful Escape cancellation, crash-safe `pending_runs` recovery
- Unified config loading (`ISessionConfigLoader`) and system instructions (`IInstructionProvider`) for
  CLI agents and subagents
- Multi-provider support — DeepSeek / OpenAI / Anthropic / Gemini usage parsing; `DeepSeek` config
  section renamed to `LLM`
- Serilog file logging (`~/.glyphite/logs/`), markdown table rendering, per-turn config hot-reload
- Also present in this window: `Glyphite.Gui` (Avalonia) experiment, later dropped

See `git log` for the full history.

## [1.0.0] — 2026-06-23

### Added

- Unit test project (xUnit + NSubstitute) with 119 tests covering configuration validation, data layer
  (SessionRepository, BlockRepository), ConfigService, and FilePatchTool
- CI/CD via GitHub Actions (build → test on push/PR to main)
- CHANGELOG.md for release tracking

### Changed

- Dependency versions pinned (no more floating `10.0.*` ranges)
- Version bumped from 0.8.15 → 1.0.0
- CodeGraph index initialized for IDE-level code navigation
- README updated with test project info and v1.0.0 references

## Previous (v0.x)

See git log for full history of pre-release development.
