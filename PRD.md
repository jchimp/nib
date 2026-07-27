# Nib — Product Requirements

**Status:** draft, phase 1 in progress
**Owner:** Jeremy
**Type:** personal project

---

## 1. Problem

Editing a config file on a Windows box from the terminal has no good answer.

- `notepad` leaves the terminal, has no syntax highlighting, and mangles line
  endings by habit.
- VS Code takes seconds to launch and is absurd for a four-line `.ini` change.
- nano under WSL doesn't see Windows paths cleanly and isn't available on a bare
  server.
- The PowerShell ISE is deprecated and was never an editor for anything but
  PowerShell.

What's missing is what nano is on Linux: a small, immediate, console-native
editor you invoke without thinking, use for ninety seconds, and close.

## 2. Users

One: me. Windows daily driver, terminal-heavy, Windows Terminal, a lot of time in
`.ini`, `.yaml`, `.toml`, `.ps1`, `.py`, `.sql`, and `Dockerfile`-adjacent config
across a homelab and a work network.

Designing for one user is a feature. It means every ambiguous call gets resolved
by "what would I actually want" rather than by committee.

## 3. Goals

1. **Immediate.** Under 100 ms from `nib file.yaml` to a painted screen.
2. **Colored.** Real syntax highlighting for the file types I actually edit, with
   VS Code themes so the colors match what I already read all day.
3. **Familiar in both directions.** Ctrl+C / Ctrl+V / Ctrl+Z / Ctrl+S do what
   every other Windows program does. nano's Ctrl+K, Ctrl+U, Ctrl+O, Ctrl+W still
   work.
4. **One file to deploy.** Copy `nib.exe` onto a box, done.
5. **Never corrupts a file.** Line endings and encoding survive a round trip
   untouched. This outranks every other goal.

## 4. Non-goals

Explicitly out of scope, permanently:

- Language servers, autocomplete, go-to-definition, linting
- Multiple buffers, tabs, split panes, a file tree
- Plugins or an extension API
- Cross-platform. Windows console only
- Editing files larger than a few tens of MB
- Git integration
- Configurable keybindings (v1 — maybe later, but not a launch requirement)

## 5. Requirements

### 5.1 Editing — must have

- Open by path argument or start empty; save, save-as, quit with a modified-file
  prompt
- Insert, delete, backspace, newline, tab
- Cursor: arrows, Home/End, PgUp/PgDn, Ctrl+Home/Ctrl+End, Ctrl+arrow by word
- Selection via Shift+movement, with Ctrl+Shift+arrow by word
- Cut / copy / paste through the system clipboard
- Undo / redo, with consecutive typing coalesced into one undoable action
- Search, search-again, and replace with confirm-each
- Go to line number
- Horizontal scrolling for long lines (no soft wrap in v1)

### 5.2 File handling — must have

- **Line endings preserved as loaded.** A CRLF file saves as CRLF. Mixed endings
  keep whatever each line had. Never silently normalize; a config file that comes
  back with different endings can break deployments in ways that are miserable to
  trace.
- **Encoding preserved.** UTF-8 with and without BOM, UTF-16 LE/BE with BOM.
  Detect on load, write back the same. Default new files to UTF-8 no BOM.
- Tabs rendered at a configurable width, default 4, stored as tabs. Byte column
  and display column tracked separately.
- Read-only files open read-only with a clear indication, not a save failure at
  the end.
- Atomic save: write to a temp file in the same directory, then replace. A power
  cut mid-save must not leave a truncated config.

### 5.3 Highlighting — must have

Thirteen languages, chosen as the actual contents of a config directory:

INI · XML · TOML · YAML · SQL · Python · JavaScript · Rust · Go · Bash ·
PowerShell · Batch/CMD · JSON · Markdown

Detection by file extension, then by filename (`Cargo.lock`, `docker-compose.yml`),
then by shebang. Manual override via a command.

Themes: Dark+, Light+, Monokai, Solarized Dark, High Contrast. Dark+ default.

Unknown file type opens uncolored. That is a normal state, not an error.

### 5.4 Interface — must have

- Two-line shortcut bar at the bottom, nano-style
- Status line: filename, modified marker, cursor position, detected language,
  encoding, line-ending style
- Prompt line for save-as, search, replace, and go-to-line, with Esc to cancel
- Message line for transient feedback ("Wrote 42 lines")

### 5.5 Nice to have — after v1

- Mouse: click to position cursor, drag to select, wheel to scroll (input plumbing
  already exists from phase 1)
- Bracket matching
- Soft wrap toggle
- Config file at `%APPDATA%\nib\config.toml`
- Trailing whitespace visualization

## 6. Keybindings

| Key | Action | Note |
|---|---|---|
| Ctrl+C | Copy selection, else copy current line | VS Code behavior when nothing is selected |
| Ctrl+X | Cut selection, else **quit** | Selection disambiguates; preserves nano's quit |
| Ctrl+V | Paste | |
| Ctrl+U | Paste | nano's "uncut" |
| Ctrl+K | Cut current line | nano |
| Ctrl+Z | Undo | |
| Ctrl+Y | Redo | Ctrl+Shift+Z also |
| Ctrl+S | Save | |
| Ctrl+O | Save | nano's "write out" |
| Ctrl+F | Search | |
| Ctrl+W | Search | nano's "where is" |
| Ctrl+R | Replace | |
| Ctrl+G | Help | nano |
| Ctrl+A | **Select all** | See below |
| Ctrl+E | End of line | nano |
| Ctrl+L | Go to line | |
| Home / End | Start / end of line | |
| Shift+movement | Extend selection | |
| Esc | Cancel prompt, clear selection | |

### The Ctrl+A decision

nano uses Ctrl+A for beginning-of-line; every Windows application uses it for
select-all. Both cannot be true.

**Ctrl+A is select-all.** Home already covers beginning-of-line and is what
muscle memory reaches for on a Windows keyboard anyway. Ctrl+E survives as
end-of-line because nothing contests it.

This is the one place the design knowingly breaks nano. Revisit after two weeks
of real use.

### The Ctrl+C / Ctrl+X pattern

Both keys are overloaded, resolved by whether a selection exists. It sounds
fragile and isn't: with a selection you unambiguously mean cut/copy, and without
one, quitting on Ctrl+X is exactly the nano reflex. The status bar shows the
active meaning.

nano's actual Ctrl+C (report cursor position) is dropped. The status line shows
the position permanently, so the command has nothing to do.

## 7. Performance targets

| Metric | Target | Notes |
|---|---|---|
| Launch to first paint | < 100 ms | Grammar loading must not be on this path |
| Full 200×50 redraw | < 1 ms | Cell diff, one `WriteConsoleW` |
| Keystroke to paint | < 16 ms | One frame |
| Re-tokenize after edit | < 5 ms typical | Carry-state convergence, usually 1–2 lines |
| Open a 5 MB file | < 500 ms to interactive | Colors may still be arriving |
| Binary size | < 10 MB | ~95 KB of that is grammars |

## 8. Distribution

Framework-dependent single-file `nib.exe`, ReadyToRun, targeting `win-x64` and
.NET 10. Requires the .NET runtime on the target machine.

Rejected: self-contained (~15 MB for no benefit on machines that already have the
runtime) and NativeAOT (TextMateSharp's reflection and native interop make it
more trouble than the startup win is worth at this size).

## 9. Success criteria

Nib is done when I stop typing `notepad` — and when, six months later, I haven't
lost a file to it.
