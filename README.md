# nib

A nano-style console text editor for Windows. Single binary, TextMate syntax
highlighting, CUA clipboard bindings over nano muscle memory.

Notepad is worse than nothing, and `notepad.exe` breaks flow by leaving the
terminal. nano over WSL doesn't see Windows paths cleanly. nib is a fast, colored,
console-native editor for config files that opens immediately and closes without
ceremony.

It is deliberately not an IDE: no LSP, no completion, no project tree, no plugins,
no multiple buffers.

## Install

Download `nib-<version>-win-x64.zip` from the release, extract it, and run:

```powershell
.\install.ps1
```

That copies `nib.exe` to `%LOCALAPPDATA%\Programs\nib` and adds it to your user
PATH. No admin rights. Open a new terminal, then:

```powershell
nib --version
nib config.toml
```

To see what it will do first, `.\install.ps1 -WhatIf`. To remove it,
`.\install.ps1 -Uninstall`.

If you'd rather skip the script, `nib.exe` is self-contained — drop it anywhere on
your PATH and it works. There are no prerequisites; the .NET runtime is inside the
exe.

## Usage

```
usage: nib [options] [+LINE] [file]

  --theme <id>   dark-plus | light-plus | monokai | solarized-dark | high-contrast
  -l, --line-numbers, --no-line-numbers
  --tab-width <n>          1-16, default 8
  --mouse, --no-mouse
  --no-config              ignore config.toml
  -h, --help
  -v, --version
```

A file that does not exist opens as a new, empty buffer already named for it, so
`nib newthing.conf` then Ctrl+S writes the file. `+LINE` opens at a line:
`nib +200 app.log`.

## Settings

Optional, at `%APPDATA%\nib\config.toml`. Nib reads this file and never writes it;
if it isn't there you get the defaults shown.

```toml
[editor]
theme = "dark-plus"
tab_width = 8
mouse = false
line_numbers = false
```

A command-line flag beats the file. A line nib can't read is reported on the
message row and skipped — the rest of the file still applies, and a bad setting
can never stop the editor opening.

`mouse` is off by default, which leaves the terminal its own drag-select-and-copy.
Turning it on hands those events to nib instead, and nib does not yet do anything
with them — so it is off until clicks position the caret.

| Keys | |
|---|---|
| Arrows / Home / End / PgUp / PgDn | Move |
| Ctrl+arrows | Move by word |
| Shift+move | Select |
| Esc | Clear the selection |
| Ctrl+A | Select all |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste |
| Ctrl+K / Ctrl+U | Cut line / paste line |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+S or Ctrl+O | Save |
| Ctrl+Q | Quit |
| Ctrl+X | Cut, or quit with no selection |
| Ctrl+G | Go to line |
| Ctrl+H | Help |
| Alt+T | Cycle theme |
| Alt+N | Toggle line numbers |

Ctrl+Q is the exit key; Ctrl+X keeps nano's context-dependent meaning so both
habits work. Copying collapses the selection rather than leaving it painted.

Highlighting covers 16 languages using real VS Code grammars — JSON, YAML, TOML,
INI, XML, Markdown, SQL, Python, JavaScript, TypeScript, TSX, Rust, Go, Bash,
PowerShell and Batch. Language is detected from the extension, the filename, or a
shebang.

## Building from source

See [BUILD.md](BUILD.md). Requires the .NET 10 SDK and Windows.

## Docs

- [BUILD.md](BUILD.md) — building, testing, releasing
- [ARCHITECTURE.md](ARCHITECTURE.md) — how it's put together
- [ROADMAP.md](ROADMAP.md) — phases and what's left
- [CLAUDE.md](CLAUDE.md) — design decisions and the Win32 landmines behind them

## License

MIT. See [LICENSE](LICENSE).
