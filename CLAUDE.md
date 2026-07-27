# CLAUDE.md — Nib

A nano-style console text editor for Windows. C#, single binary, TextMate syntax
highlighting, CUA clipboard bindings over nano muscle memory.

Personal project. Not Simkins work.

---

## Why this exists

Notepad is worse than nothing and `notepad.exe` breaks flow by leaving the
terminal. nano over WSL doesn't see Windows paths cleanly. The gap is a fast,
colored, console-native editor for config files that opens in under a tenth of a
second and closes without ceremony.

Scope discipline: **this is an editor for config files and short scripts.** It is
not an IDE. No LSP, no completion, no project tree, no plugins, no multiple
buffers. Every feature request gets measured against "would nano have this?"

---

## Stack

| Decision | Choice | Why |
|---|---|---|
| Language | C# / .NET 10 | Short P/Invoke story for the two hard parts; fast enough |
| Distribution | Framework-dependent single file, ReadyToRun | Needs .NET runtime installed; Jeremy has it |
| Rendering | Hand-rolled VT sequences over `WriteConsoleW` | No TUI framework. Full control, no dependency |
| Input | `ReadConsoleInputW` (INPUT_RECORD) | Structured records; sees resize and mouse, which `Console.ReadKey` cannot |
| Highlighting | TextMateSharp engine + our own vendored grammars | Real VS Code grammars and themes, curated file set |
| Buffer | `List<Line>`, whole file in memory | Target files are a few MB. Anything cleverer is unjustified |

`net8.0` also works if `net10.0` isn't installed. Change one line in the csproj.

---

## Layout

```
src/Nib/
  Program.cs
  Terminal/      NativeMethods, ConsoleHost, TerminalWriter, InputReader,
                 InputEvents, Clipboard, Screen (phase 2)
  Model/         TextBuffer, Line, Cursor, Selection, Undo/, FileIo   (phase 3)
  Highlight/     TextMateHighlighter, GrammarStore, ThemeMap,
                 TokenizeScheduler                                    (phase 5)
  Ui/            EditorView, StatusBar, HelpBar, Prompt               (phase 3+)
  Commands/      Keymap, Commands                                     (phase 3+)
  Resources/     Grammars/*.json.gz, Themes/*.json.gz  (generated, committed)
grammars/manifest.json
tools/fetch-grammars.ps1
tests/Nib.Tests/
```

### The one architectural rule

**`Model/` must never reference `Terminal/`.**

The entire editing core — buffer, cursor, selection, undo — has to be testable
without a console. Undo and selection interact in ways that produce subtle bugs,
and finding those through a TUI is miserable. If you find yourself wanting a
console handle inside `Model/`, the design is wrong.

`Ui/` depends on both. `Terminal/` depends on nothing.

---

## Build

```powershell
dotnet build src/Nib/Nib.csproj
dotnet run   --project src/Nib/Nib.csproj
dotnet test  tests/Nib.Tests/Nib.Tests.csproj

dotnet publish src/Nib/Nib.csproj -c Release
# -> src/Nib/bin/Release/net10.0/win-x64/publish/nib.exe
```

Refresh vendored grammars (not part of the build; the `.gz` files are committed):

```powershell
./tools/fetch-grammars.ps1 -Force
```

---

## Win32 landmines

These are all things that cost real time to diagnose. They are written down so
they cost that time once.

**Restore the console on every exit path.** Normal return, unhandled exception,
Ctrl+Break, and the window's close button are four separate paths. `ConsoleHost`
hooks all of them and `Restore()` is idempotent via `Interlocked`. Dying in raw
mode leaves the user's shell with no echo and no line editing — genuinely
hostile. Test this by killing the process mid-run.

**Root the console control handler delegate.** `SetConsoleCtrlHandler` stores a
raw function pointer. If the delegate is only reachable from a local, the GC
collects it and Windows calls into freed memory when the user closes the window.
It lives in a `static` field for this reason. Do not "tidy" it into an instance
field.

**`ENABLE_VIRTUAL_TERMINAL_INPUT` is deliberately OFF.** It converts keystrokes
into VT escape sequences delivered through `ReadFile`. We use `ReadConsoleInputW`
instead, which gives virtual key codes, modifier state, resize, and mouse in one
structured stream. Turning both on means parsing escape sequences for no gain.
(`ENABLE_VIRTUAL_TERMINAL_PROCESSING` on *output* is required and is on.)

**`ENABLE_PROCESSED_INPUT` off is what frees Ctrl+C.** This is the whole
mechanism. With it cleared, Ctrl+C arrives as an ordinary key event and no
`CTRL_C_EVENT` is generated at all.

**Clearing `ENABLE_QUICK_EDIT_MODE` requires setting `ENABLE_EXTENDED_FLAGS` in
the same call.** Otherwise the clear is silently ignored and you get no mouse
input with no error.

**AltGr looks exactly like Ctrl+Alt.** European layouts report AltGr as
RightAlt + LeftCtrl. Treating that as a chord means German users cannot type `@`
or `\`. `InputReader` checks for that combination plus a printable character and
treats it as text. Do not remove this because it seems like dead code on a US
layout.

**Open `CONIN$`/`CONOUT$`, not `GetStdHandle`.** Std handles may be redirected
(`nib file | more`, launched from a wrapper). nano does the same.

**`DISABLE_NEWLINE_AUTO_RETURN`** stops the console scrolling the whole buffer
when we write the bottom-right cell — which a full-screen renderer does on every
frame.

**Clipboard ownership transfers on success.** After `SetClipboardData` returns
non-zero, the system owns the `GlobalAlloc` block. Freeing it is a use-after-free
for every other application on the machine.

**Never use per-character `Console.Write`.** One buffered `WriteConsoleW` per
frame. `WriteConsoleW` takes UTF-16 directly, so there is no console code page to
set and no encoding to get wrong.

---

## Rendering model (phase 2)

Front and back grids of cells (`char`, fg, bg). Each frame: draw into the back
grid, diff against the front, emit minimal cursor-move + SGR runs into
`TerminalWriter`, one flush. Swap.

Budget: a full 200×50 redraw well under a millisecond. If it isn't, the diff is
wrong or something is flushing mid-frame.

24-bit color via `ESC[38;2;r;g;bm`. Windows Terminal renders it exactly. If
`ENABLE_VIRTUAL_TERMINAL_PROCESSING` fails to set, we're on ancient conhost —
fall back to no highlighting rather than trying to approximate a theme in 16
colors.

---

## Highlighting model (phase 5)

TextMateSharp's API is line-at-a-time with an explicit carry state:

```csharp
ITokenizeLineResult r = grammar.TokenizeLine(line.Text, stateIn, MaxTokenizeTime);
line.Tokens   = r.Tokens;
line.StateOut = r.RuleStack;
```

Cache `StateIn` and `StateOut` per line. On edit, re-tokenize forward from the
dirty line and **stop as soon as a line's new `StateOut` equals its cached
`StateOut`** — the rest of the file is unaffected. In practice that's one or two
lines. This is what makes highlighting free on a 20k-line file.

**Always pass a real timeout**, not `TimeSpan.MaxValue`. Oniguruma will backtrack
forever on a minified JavaScript line. VS Code does the same thing for the same
reason. Start at 50 ms.

**Never tokenize on the render path.** First paint is uncolored. Tokenize the
visible window synchronously, background the rest, marshal results back through a
concurrent queue drained on the render tick.

### Grammars

19 files, ~777 KB raw, ~95 KB gzipped, embedded as resources. Sources and scope
names are in `grammars/manifest.json`, all verified against the live repos.

**We do not reference the `TextMateSharp.Grammars` NuGet package.** It ships 40+
languages and 20 themes we'd never open and is the single biggest contributor to
binary size. Engine only; bring our own grammars.

Two things about the set that will otherwise waste an afternoon:

- **YAML is six files.** `yaml.tmLanguage.json` is a dispatcher that delegates to
  `source.yaml.1.2` and `source.yaml.embedded`, which cross-reference the 1.0,
  1.1 and 1.3 variants. Load the dispatcher alone and you get zero highlighting
  with no error.
- **TOML comes from taplo, not VS Code.** VS Code ships no TOML grammar. The
  obvious alternative, `textmate/toml.tmbundle`, is plist format —
  [TextMateSharp loads JSON grammars only](https://github.com/danipen/TextMateSharp),
  so it cannot be used.

Markdown references 61 external scopes for fenced code blocks. The 13 languages
we ship will highlight inside fences; the rest render as plain text. That is
correct degradation, not a bug.

### Native dependency

TextMateSharp wraps Oniguruma through the `Onigwrap` native library. Consequences:

- `IncludeNativeLibrariesForSelfExtract=true` is required for single-file
  publish. First launch extracts to `%TEMP%\.net\nib\<hash>`.
- There is a [historical heap-corruption report](https://github.com/dotnet/runtime/issues/65443)
  combining Onigwrap with `PublishSingleFile`. Old, probably fixed. **Verify at
  the start of phase 5**, not the end — soak-test a published single-file build
  opening and closing many files before building anything on top of it.

Put the highlighter behind `IHighlighter` from day one so the engine is
swappable if that goes badly.

---

## Coding conventions

- File-scoped namespaces, nullable enabled, `TreatWarningsAsErrors`.
- No third-party packages beyond TextMateSharp. If something needs a library,
  first ask whether it needs doing.
- Comments explain *why*, especially in `Terminal/` where the code is a
  transcription of undocumented-feeling Win32 behavior. Don't restate the call.
- Allocation discipline in the render and input paths only. Elsewhere, clarity
  wins — this is a config-file editor, not a database.
- Ordinal string comparison everywhere. `InvariantGlobalization` is on.

---

## Status

- **Phase 1 — terminal foundation: verified on hardware (2026-07-26).**
  `Program.cs` is an interactive probe, not an editor. All five checks confirmed
  on a real console; the foundation is cleared for phase 2.
- Phases 2–6: see `docs/ROADMAP.md`.

### Correction carried forward

Early planning said to enable `ENABLE_VIRTUAL_TERMINAL_INPUT` on stdin *and* use
`ReadConsoleInputW`. Those are contradictory. The INPUT_RECORD path is the right
one, so VT input mode is off. Noted here because the wrong version may appear in
older notes.
