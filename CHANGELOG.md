# Changelog

All notable changes to Glyphite are documented here, newest first. Versions come from
`version.txt`, bumped by `./publish.sh`.

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
