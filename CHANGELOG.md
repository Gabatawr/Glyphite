# Changelog

All notable changes to Glyphite will be documented in this file.

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
