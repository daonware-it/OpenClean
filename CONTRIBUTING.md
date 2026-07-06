# Contributing to OpenClean

Thanks for your interest in improving OpenClean! This document explains how to
report issues, propose changes, and get a pull request merged.

## Ground rules

OpenClean is built on a simple promise: **honest system cleaning — real
measurements, no telemetry, no dark patterns.** Contributions must respect that.
In particular:

- **No telemetry, tracking, or phone-home** in the free application.
- **No fake results** — every number shown to a user must be a real measurement.
- OpenClean **never deletes programs itself**; it always invokes the vendor's own
  uninstaller. Keep it that way.

## Scope (open-core)

OpenClean follows an open-core model. This repository contains the open-source
application. The proprietary **Premium module** (scheduled cleaning, batch
uninstall) lives outside this repository and is not part of contributions here.

## Reporting bugs & requesting features

Please use the [issue templates](.github/ISSUE_TEMPLATE). Before opening a new
issue, search existing ones to avoid duplicates. For security-relevant problems,
follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Development setup

Requirements: **Windows 10/11** and the **.NET 10 SDK**.

```bash
git clone https://github.com/daonware-it/OpenClean.git
cd OpenClean
dotnet build OpenClean/OpenClean.csproj -c Debug
dotnet run   --project OpenClean/OpenClean.csproj
```

OpenClean is a WPF app targeting `net10.0-windows` and builds/runs on Windows only.

## Pull requests

1. Fork and create a feature branch off `main`.
2. Keep changes focused; one logical change per PR.
3. Match the existing code style (`.editorconfig` is enforced).
4. Make sure the solution builds: `dotnet build OpenClean.sln -c Release`.
5. If your change is user-facing, add a note under "Unreleased" in
   [CHANGELOG.md](CHANGELOG.md).
6. Fill out the pull request template and link any related issue.

## Internationalization

OpenClean ships 7 UI languages. Every declared language needs a complete
translation file — if you add or change a UI string, update **all** language
files, not just the English one.

## License of contributions

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE) that covers this repository.
