# Changelog

All notable changes to Glyphite will be documented in this file.

## [1.3.24] — 2026-07-24

### Added
- **Interactive confirmation** for dangerous bash commands — panel `[OK] [Stop] [Check]` with arrow keys, timer, and KVStore last-choice persistence
- **SafetyChecker** — LLM-powered safety evaluation for shell commands (subagent auto-check and "Check" panel option). Reads conversation context, returns `{allow, why}`, records usage
- **Reasoning auto-compaction** — large `agent_reasoning` blocks >6K chars are automatically LLM-compacted after each turn (replaces old PeekReasoning system)
- `CheckRequireCommands` and `CheckRequireCommandsTimeout` in `BashOptions` — 50+ command patterns for dangerous operations
- `AutoCompressReasoning` / `AutoCompressReasoningMaxSize` in `CompressionOptions`
- `kv_store` cleanup on agent deletion (`DELETE FROM kv_store` added to `DeleteSessionAsync`)

### Fixed
- `MaxSize: 0` now correctly hides output from LLM (was being treated as "unlimited")
- `ToolStreaming` config binding — direct array loading instead of `GetOptionsAsync<T>` (which mismatched flat config keys with `Entries` property)
- `InteractiveConfirmation` Task.Run leak — `pendingRead ??=` ensures at most 1 blocked thread in pool (was accumulating infinitely)
- Wildcard `*` support in `GetHiddenArgs` — mirrors existing `GetMaxLength` logic

### Changed
- `ToolExecution.MaxSize` semantics: `0` = hide, `-1` = unlimited, `N > 0` = first N chars. Truncation shows 1/3 top + notice + 2/3 bottom, full output saved to temp file
- Truncation logic centralized in `ToolConfigDecorator` — all tools (builtin + MCP) use the same 1/3+2/3 + temp file pattern
- Removed protected blocks system (dead code after compaction change — all blocks now go to LLM, all are hard-deleted)
- Removed dead fields: `DefaultTimeoutMs` (Bash), `TimeoutSeconds` (WebFetch), `MaxContentLength` (WebFetch), `MaxOutput` (Bash), `MaxReadChars` (Search) — replaced by `ToolExecution.Timeout`/`MaxSize`
- `read_file` returns hard error when file exceeds `ToolExecution.MaxSize` (no temp file written)
- Removed `PeekReasoning` / `PeekToolReasoning` from config — replaced by `AutoCompressReasoning`
- `appsettings.json` updated: Cyrillic replaced with English labels (OK/Stop/Check), new Compression/AutoCompress settings, new Bash/CheckRequireCommands

## [1.0.0] — 2026-06-23

### Added
- Unit test project (xUnit + NSubstitute) with 119 tests covering configuration validation, data layer (SessionRepository, BlockRepository), ConfigService, and FilePatchTool
- CI/CD via GitHub Actions (build → test on push/PR to main)
- CHANGELOG.md for release tracking

### Changed
- Dependency versions pinned (no more floating `10.0.*` ranges)
- Version bumped from 0.8.15 → 1.0.0
- CodeGraph index initialized for IDE-level code navigation
- README updated with test project info and v1.0.0 references

### Previous (v0.x)

See git log for full history of pre-release development.
