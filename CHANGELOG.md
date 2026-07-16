# Changelog

All notable changes to OpenClean are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.2.0] - 2026-07-16

### Added
- Safety net for deletions: OpenClean can create a Windows restore point before
  a cleanup and back up files before deleting them, so a whole run can be undone.
  Both nets are individually switchable.
- New "Restore" view: backed-up runs from every area (Cleanup, Duplicates,
  Leftovers, Privacy, Large files) can be undone or their backup discarded.
- Existing Windows restore points are listed in their own section of that view.
  Single points can be deleted, and "Delete old" removes all but the most recent
  one. Rolling the system back stays with Windows' own tool (rstrui.exe).
- Storage & RAM: sunburst usage analysis – click a segment to zoom into a folder
  and see what actually occupies the drive.
- Large files finder as its own tab, including moving files to the Recycle Bin.
  Before deleting, files that do not fit in the Recycle Bin (quota, "delete
  immediately" setting, drives without a bin) are named explicitly – they would
  be removed permanently.
- Context menu management: entries in the Windows right-click menu can be turned
  off; nothing is deleted.
- Delayed startup: startup programs can be staggered after sign-in instead of all
  launching at once.
- License page with activation status, device release and an overview of the Pro
  features.

### Changed
- Moving files to the Recycle Bin is now batched: up to 128 paths go into a
  single shell call instead of one call per file, which speeds up cleanups with
  many temporary files considerably.
- Scheduled cleaning and batch uninstall moved into the paid Premium module
  (open-core); the core of OpenClean stays free and open source.

### Fixed
- Restore point timestamps were shown shifted by the local UTC offset because
  the WMI date's offset was cut off instead of evaluated.
- Large files deleted from the finder did not appear in the Restore view, so
  they looked permanently gone even though they were in the Recycle Bin.

### Security
- Self-check on startup: OpenClean verifies its own signature and locks the
  modifying functions if the file was altered after signing. Builds from source
  are unsigned and keep working – they only show a hint.
- The native DLL search path is restricted to System32, so a DLL placed next to
  the executable can no longer be loaded instead of the real system library.

## [1.1.0] - 2026-07-11

### Added
- Real application icons in the Updates view: winget updates are matched to the
  installed programs' registry entries and show the actual program icon, with a
  colored letter avatar as fallback.
- Real application icons in the Startup view, extracted from each entry's
  resolved executable (letter avatar for Store apps and unresolvable commands).
- Schedule: new "Report storage" section – reports after automatic runs can be
  turned off entirely, and the history retention is selectable (10/30/100).

### Changed
- Startup, Privacy, Updates, Schedule and Duplicates views were aligned with
  the redesigned look of Overview, Storage & RAM, Cleanup and Uninstall (icon
  buttons, icon chips, mono metrics, soft badges, animated loading bars).
- Schedule view redesigned: frequency pills, cleaner profile rows with
  RECOMMENDED/RISK tags, outline icons and restructured cards.
- Available versions in the Updates view are highlighted as accent badges and
  version numbers use the monospace metric font.
- Repository product screenshots were refreshed to reflect the redesigned UI
  (Overview, Storage & RAM, Cleanup, Uninstall and Software updates), all
  captured with the English UI.

## [1.0.2] - 2026-07-11

### Fixed
- The per-drive usage line in the Storage & RAM view (and its space warnings)
  was always shown in German ("… von … belegt · … frei"), even when the app
  language was set to English or another language; it now uses the localized
  strings for every language.

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

[Unreleased]: https://github.com/daonware-it/OpenClean/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/daonware-it/OpenClean/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/daonware-it/OpenClean/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/daonware-it/OpenClean/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/daonware-it/OpenClean/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/daonware-it/OpenClean/releases/tag/v1.0.0
