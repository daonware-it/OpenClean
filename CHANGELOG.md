# Changelog

All notable changes to OpenClean are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.1] - 2026-07-07

### Added
- Recycle Bin cleanup now previews the actual items it contains (name, original
  path, size) read from the `$Recycle.Bin` metadata, and deletes them
  individually instead of as a single combined entry.
- Windows notification when an unattended cleanup (scheduled `--auto` run)
  finishes.
- Scheduled cleaning now also starts on battery power and catches up a missed
  run (PC off/standby), via a Task Scheduler XML definition.

### Fixed
- Recycle Bin size was reported far too high because orphaned `$I` metadata
  entries (whose `$R` data no longer exists) were counted; they are now skipped,
  so the size matches what Windows reports.
- Premium module downloads that hit the server rate limit (HTTP 429) were shown
  as "server unreachable"; they now show a dedicated "please wait a moment"
  message.
- An unattended run could crash itself by deleting the .NET single-file
  extraction folder in `%TEMP%`; that folder is now excluded from cleanup.

## [1.0.0] - 2026-07-06

### Added
- Overview dashboard with a real system score (reclaimable space, startup load,
  drive pressure) and one-click recommendations.
- Storage & RAM view with live memory usage and per-drive space.
- Cleanup with preview of temp files and caches before deletion.
- Startup manager for autostart entries and their boot-time impact.
- Privacy scanner to find and clear privacy-relevant traces.
- Update check for new OpenClean versions.
- Uninstall view listing programs by size, with leftover folder/registry scan;
  single uninstall is always free.
- Duplicate finder (exact duplicates by SHA-256).
- 7 UI languages: German, English, Spanish, French, Polish, Portuguese, Russian.
- Open-core Premium module: scheduled cleaning and batch uninstall.

[Unreleased]: https://github.com/daonware-it/OpenClean/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/daonware-it/OpenClean/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/daonware-it/OpenClean/releases/tag/v1.0.0
