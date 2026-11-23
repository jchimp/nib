# Changelog

Notable changes to nib, newest first. Add entries under `## Unreleased` as you
work; `scripts/release.ps1 -Version x.y.z -Apply` turns that section into the
release, and its text becomes the GitHub release notes.

## Unreleased

### Added

- `uninstall.ps1` ships in the release zip and is copied into the install
  folder. It removes the folder, its user PATH entry and the uninstall entry,
  and keeps `%APPDATA%\nib\config.toml`.
- The installer registers nib in Settings > Apps (current user only), so it can
  be uninstalled from there.
- `-DryRun` on `install.ps1` and `uninstall.ps1` prints every change and makes
  none.
- `CHANGELOG.md`, shipped in the zip. GitHub release notes now come from it.

### Changed

- `install.ps1` installs the whole release folder (exe, docs, uninstall script),
  not only `nib.exe`, and clears the downloaded-file mark so SmartScreen does
  not block the exe on every launch.
- `install.ps1` refuses to run while nib is running from the install folder,
  rather than failing halfway through the copy. It does not close nib for you,
  because an open editor may hold unsaved text.

### Fixed

- Installing and uninstalling no longer drops a trailing `;` from the user
  PATH. The PATH is now byte-for-byte what it was before the install.

### Removed

- `install.ps1 -Uninstall`: use `uninstall.ps1` or Settings > Apps.
- `install.ps1 -WhatIf`: use `-DryRun`.
- `install.ps1 -Source`: run `install.ps1` from the unzipped release folder.

## 0.7.1 (2026-09-19)

### Added

- Mouse support: click places the caret, drag selects, Shift+click extends,
  double-click selects a word, the wheel scrolls the view without moving the
  caret, right-click pastes at the caret. On by default; `--no-mouse` or
  `mouse = false` gives the terminal its own drag-select back.
- `auto_indent` config key: Enter keeps the indent of the current line.
- `tabs_to_spaces` config key: Tab inserts spaces to the next tab stop.

### Fixed

- `^O` (Save As) overwrote the current file without asking. It now always
  prompts, pre-filled with the current path, and asks before writing over a
  different existing file.
- Saving a file opened through a symbolic link failed with Win32 error 1464.
  nib now saves to the link's target and leaves the link in place.

## 0.7.0 (2026-09-08)

First tagged release.

- nano-style console editor for Windows: open, edit, save, cut/copy/paste with
  the Windows clipboard, selection, undo and redo.
- Syntax highlighting from VS Code TextMate grammars and themes for 16
  languages, including YAML, JSON, TOML, PowerShell, Python, XML, Markdown and
  TypeScript.
- Find (`^F`, `F3`, `Shift+F3`), replace (`^R`, scoped to the selection when
  there is one), go to line, and word/line/character counts (`^W`).
- Full keymap on `^H`. Line-number gutter on `Alt+N`.
- Settings in `%APPDATA%\nib\config.toml`: theme, tab width, mouse, line
  numbers.
- Self-contained release zip with a per-user installer that adds nib to the
  user PATH without admin rights.
