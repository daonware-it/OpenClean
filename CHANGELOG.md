# Changelog

All notable changes to OpenClean are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.3.0] - 2026-07-18

### Added
- Real NVMe SMART detail values for NVMe SSDs – power-on hours, power cycles and
  total bytes written (TBW) are decoded straight from the NVMe health log page,
  instead of being left empty where the generic ATA/reliability sources report
  nothing for these drives.
- Power cycles are now also read for USB drives via StartStopCycleCount, so an
  external disk no longer shows an empty value where Windows exposes the counter.
- A central settings area, reachable from the gear icon in the title bar, that
  bundles theme, language and licence/Pro management in one place.
- A theme picker in that settings area with several selectable visual styles beyond
  light and dark, each shown with a small colour preview: Cyberpunk (dark with neon
  accents, a slowly scrolling scanline and monospace figures), Gaming-RGB (a gently
  cycling accent colour), three established developer palettes – Nord, Catppuccin and
  Dracula – a Dev style (dark editor look with a terminal-green accent, monospace
  figures and a blinking terminal cursor), and a Windows 11 style (light Fluent
  palette). Themes switch live, without a restart, and every palette keeps its text
  legible on the accent colour.
- "Use Windows accent colour" as a switch that overrides the theme's accent with the
  colour set in Windows and follows it live: change it in the Windows settings and
  OpenClean follows without a restart.
- A translucent Mica/Acrylic window backdrop on Windows 11 – the real one, through the
  Windows composition API, not a faked blur. The Windows 11 theme turns it on
  automatically. On Windows 10 the switch is disabled and the window stays opaque
  rather than pretending to blur.
- A "reduce animations" switch that stops the moving effects – the Gaming-RGB accent
  cycle, the Cyberpunk scanline scroll and neon-glow pulse, and the Dev cursor blink –
  while keeping the static look, for readability and battery.
- Deep system cleanup as its own tab, covering five areas Windows keeps to
  itself: the previous installation (Windows.old), the component store (WinSxS),
  shadow copies, the Windows Update cache (SoftwareDistribution) and the
  Delivery Optimization cache. Every area is measured before anything is
  touched and confirms separately before it deletes.
- Occupied space and reclaimable space are shown as two different numbers,
  because they rarely match: of a 22 GB component store roughly a third is
  shared with Windows and can never be freed, and shadow copies keep their most
  recent restore point on purpose. Showing only one of the two would flatter the
  result.
- Freed space is measured before and after, never estimated from what a tool
  claims to have done.
- The component store analysis reports real progress and an estimated time
  remaining – the figures DISM itself reports, not a guess. Areas that report no
  progress name the area they are working on instead of inventing a percentage.
- Network transparency (local) as its own tab: a live view of which programs
  hold connections to the outside, with process name and full path, remote
  address and port, and connection state. Sortable by process, destination or
  state, filterable by text; connections that never leave the machine can be
  hidden.
- The network view only reads. It never blocks, throttles or alters a
  connection, and never looks at content. It polls every two seconds and only
  while the view is open. Optional reverse DNS lookup is off by default: it
  sends every remote address to your DNS resolver, which also makes OpenClean
  itself show up in the list.
- Drive health in "Storage & RAM": every physical disk gets a verdict – healthy,
  keep an eye on it, or failure likely – read locally from SMART through Windows.
  No vendor tool, no online lookup, nothing is changed.
- The readings come from the raw ATA SMART attributes where Windows exposes
  them, and fall back to the storage reliability counters where it does not,
  which is typical for NVMe drives. Whatever no source reports stays empty
  instead of being shown as zero: a USB enclosure that passes no SMART data
  through says "not available" rather than claiming to be healthy.
- The individual readings – temperature, life remaining, power-on hours, power
  cycles, reallocated and pending sectors, total bytes written – are part of
  OpenClean Pro. The verdict itself stays free: the question "do I need to back
  up?" should not cost anything.
- The cookie inventory on the privacy page now lists the cookie names per domain,
  not just how many there are. Names and hosts are stored in plain text by both
  Chromium and Firefox; only the cookie value is encrypted, and OpenClean never
  reads it. Nothing has to be decrypted to show you which cookies exist.
- A search box above the cookie list filters live by domain or by cookie name, so
  a single site can be found among several hundred without scrolling.
- Cookie databases that a running browser holds open are now readable. Chrome
  keeps its cookie database exclusively locked while it runs, so the list
  previously stayed empty with the browser open. OpenClean now reads the file's
  clusters straight from the volume instead of opening the file, which the lock
  does not cover. Nothing on the system is created, changed or deleted for this –
  in particular no volume shadow copy, because releasing one forces Windows to
  discard every older shadow copy on the drive, and that would take your restore
  points with it.
- Before cookies are deleted, OpenClean now names the browser that is still
  running. Reading works through the raw access above, but deleting needs the
  real file: with the browser open the cleanup would otherwise create a restore
  point and then report nothing removed, without explaining why.

### Changed
- The entry in "Add or remove programs" is now named "OpenClean" without the
  version number – Windows lists the version in its own column anyway.
- Better dark-mode contrast for the drive-health detail values, so the readings
  stay legible against the dark surface.
- The light/dark toggle in the settings area became a theme grid; light and dark are
  now two entries among the other styles.
- The accent glow on the primary button and the active sidebar item follows the
  theme's accent colour instead of a fixed green, so it matches every palette and can
  pulse in the Cyberpunk style.
- The version shown in the sidebar footer is now read from the application
  version at runtime instead of being hard-coded in every language file. The
  version number lives in one place; each translation only keeps its localised
  suffix.
- Cleanup and Deep Clean are now grouped under a single expandable "Cleanup"
  entry in the sidebar instead of two separate items. The group expands to the
  two options and opens itself automatically when either is active. Both actions
  keep their exact previous behaviour, tooltips and bindings.
- Internal code restructuring toward MVVM and clean-code principles, with no
  change to behaviour or performance. Every dialog and UI-thread access used by
  the view models now goes through interfaces (IDialogService/IUiDispatcher)
  instead of touching WPF directly, so the view-model logic is unit-testable; the
  window-owned safety prompt and the main view model were freed of their WPF and
  concrete-view references (the locked schedule area is now selected by a data
  template). The largest classes were split up: the raw-volume reader's 190-line
  copy routine was broken into named steps; the 630-line Recycle Bin helper became
  a thin facade over focused policy, shell, inventory and STA-thread parts; the
  dashboard's score and recommendation logic and the duplicate "keep at least one
  copy" guard were extracted into standalone, unit-tested classes; and the
  cancellable-scan scaffolding the cleanup, deep-clean and duplicate views each
  copied was consolidated into one shared base class.

### Performance
- The main lists – installed programs, network connections, startup entries,
  updates and context-menu entries – are now UI-virtualised: only the rows
  actually on screen are built and recycled while scrolling, instead of
  realising every item (and its icon) up front. Opening these pages and
  scrolling them stays fast and light even with hundreds of entries.
- The installed-programs list no longer re-evaluates its sort and filter once
  per item while it fills. A single deferred refresh replaces what was
  quadratic work on lists of a few hundred programs, so the Uninstall page no
  longer stutters while loading or after a removal.
- The "Restore" area no longer reads its backup sessions and restore points on
  the UI thread. Listing, deleting and refreshing now run in the background, so
  opening the area and every refresh stay responsive; a failing restore-point
  query can no longer surface as an unobserved exception.
- The RAM waveform on the System page no longer allocates its gradient, pen and
  point buffer on every frame or looks up theme colours dozens of times a
  second. The drawing resources are cached and rebuilt only when the colour
  actually changes, cutting idle CPU and GC load while the page is open.
- Image previews in the duplicate comparison are decoded once and cached
  instead of being re-read from disk on every access, so scrolling through
  image groups no longer repeats the decode work.

### Removed
- The separate "Pro" entry at the bottom of the sidebar. Licence status and
  activation/deactivation now live entirely in the central settings area (gear
  icon), where the same view was already available – the sidebar entry had
  become redundant.

### Fixed
- The cookie scan silently returned nothing. A locked database threw an error
  that was swallowed, and the caller was still handed a path to a file that had
  never been written – it then read that missing file and reported no cookies at
  all. A copy that fails now says so instead of pretending to have worked.
- The "Cookies" category stayed empty during a privacy scan while the inventory
  above it was full, because only the inventory could read a locked database.
  Both now take the same path.
- The privacy page could not be scrolled: the upper block sat in an auto-sized
  row that grew with its contents and squeezed the category list down to a few
  pixels, leaving "Browser history" and everything below it out of reach. The
  whole page now lives in one scroll container, and the lists inside it pass the
  mouse wheel on to the page once they reach their own end.
- The cookie search box was rendered unstyled, ignoring the input styling used
  everywhere else on the page.

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

[Unreleased]: https://github.com/daonware-it/OpenClean/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/daonware-it/OpenClean/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/daonware-it/OpenClean/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/daonware-it/OpenClean/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/daonware-it/OpenClean/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/daonware-it/OpenClean/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/daonware-it/OpenClean/releases/tag/v1.0.0
