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
  Highlight/     IHighlighter, TextMateHighlighter, GrammarStore,
                 GrammarManifest, ThemeMap, LanguageDetector,
                 RawGrammarFixup, StateEquivalence, Soak            (phase 5)
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

**Fast dev loop.** `dotnet run` pays ~2–4 s of build-orchestration overhead
(restore + up-to-date check) on *every* launch. That is the dev harness, not the
editor: the compiled binary starts in ~150 ms and the startup path is trivial
(parse args → `FileIo.Load` → `ConsoleHost.Acquire` → first render). To test at
real speed, build once and run the exe directly:

```powershell
dotnet build src/Nib/Nib.csproj          # once, after code changes
./src/Nib/bin/Debug/net10.0/win-x64/nib.exe [file]

# --no-build skips the rebuild but still pays ~1.5 s of MSBuild target
# resolution — better than a full `dotnet run`, but the direct exe is the win:
dotnet run --no-build --project src/Nib/Nib.csproj -- [file]
```

Refresh vendored grammars (not part of the build; the `.gz` files are committed):

```powershell
./tools/fetch-grammars.ps1 -Force
```

**Releasing.** `./tools/release.ps1 -Version x.y.z` → `dist/nib-x.y.z-win-x64.zip`
(exe + `install.ps1` + docs + SHA256). Details in BUILD.md. Two things about it are
load-bearing:

- The release publish is **self-contained**; the csproj is not. `SelfContained=true`
  in the csproj would make every `dotnet build` copy the runtime into `bin/`, so
  `release.ps1` sets it on the publish command line instead. ~80 MB shipped vs
  ~1.3 MB for a dev build — that is the runtime, and it is what makes the zip work
  on a machine with no .NET.
- **Do not add `PublishTrimmed`.** TextMateSharp resolves grammar rule types
  reflectively; the trimmer strips them and it surfaces as a null grammar at
  runtime, not as a build error. `EnableCompressionInSingleFile` is also left off —
  it would halve the zip but decompresses on every launch, and startup is already
  135 ms against a 100 ms goal.

### PowerShell landmines

Both of these were found by running the thing, not by reading it. Same rule as
`Terminal/`: written down so they cost that time once.

**Editing user PATH corrupts it from both sides if you use the convenient API.**
`install.ps1` reads from `HKCU:\Environment` with `DoNotExpandEnvironmentNames` and
writes back through `RegistryKey.SetValue` with the value kind preserved.

- Read side: `[Environment]::GetEnvironmentVariable('Path','User')` *expands*
  `%USERPROFILE%`-style entries. Write that string back and you have silently
  replaced a variable someone wrote deliberately with a literal path.
- Write side: `[Environment]::SetEnvironmentVariable` stores a plain `REG_SZ`. A
  PATH that was `REG_EXPAND_SZ` comes back downgraded, and every surviving `%VAR%`
  in it stops expanding — the same corruption arriving from the other direction.
  This one is easy to miss because the *text* of PATH still looks perfect; only
  `GetValueKind` shows it.

Avoiding `SetEnvironmentVariable` means no `WM_SETTINGCHANGE` broadcast, so the
script sends one itself via `SendMessageTimeout` (not `SendMessage` — a hung
top-level window would block the installer indefinitely).

Jeremy's PATH really does end in `%USERPROFILE%\.dotnet\tools`, so this is not
hypothetical. Verify a change to that code by checking `GetValueKind('Path')` is
still `ExpandString` and the raw value is byte-identical after install-then-
uninstall.

**Keep `tools/*.ps1` pure ASCII.** None of them carry a BOM, so Windows PowerShell
5.1 decodes them as CP-1252, not UTF-8. A UTF-8 em dash is three bytes and the last
lands on an ASCII `"` in that code page — which terminates a string literal
mid-line and swallows everything after it. It does **not** raise a parse error: one
function absorbs the next and callers quietly run the wrong body. An em dash inside
a `Write-Host` string made `Add-ToUserPath` execute `Remove-FromUserPath`, and the
only visible symptom was a `-WhatIf` line with the wrong verb.

`ToolScriptEncodingTests` enforces this, and `release.ps1` runs the suite, so a
regression cannot reach a zip. When a script misbehaves in a way that makes no
sense, check the AST first — `Parser::ParseFile` and print each function's
`Extent.StartLineNumber`/`EndLineNumber`. A function spanning past its closing brace
names the bug immediately.

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

The control handler *also* returns `true` for `CTRL_C_EVENT`, which in normal
operation is dead code — the event never fires. It is there because the input mode
was otherwise a single point of failure with nothing behind it: anything that
restored that flag for a moment would have Windows terminate the editor mid-edit
with the buffer unsaved. Ctrl+Break is left fatal on purpose, so there is always a
way out.

**A restore path that works makes a crash look like a clean exit.** `Restore()`
runs on unhandled exceptions too, so an editor that dies on a bug puts the terminal
back neatly and simply vanishes — no stack trace on screen, no clue anything went
wrong. That is what made a real crash in the selection code (a stale anchor
surviving a line join, then Ctrl+C indexing off the end of the buffer) read as "it
escaped once". The input loop now catches per keystroke, clamps the cursor and
reports on the message row, because the buffer is still in memory and the user can
still save it. Do not "clean that up" into a top-level handler — by the time it
unwinds that far, the buffer is gone.

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
ITokenizeLineResult r = grammar.TokenizeLine(text, stateIn, MaxTokenizeTime);
```

Cache the carry state per line. On edit, re-tokenize forward from the dirty line
and **stop as soon as a line's new carry state matches its cached one** — the rest
of the file is unaffected. In practice that's one or two lines. This is what makes
highlighting free on a 20k-line file.

**The cache is a side table in `Highlight/`, not fields on `Model/Line`.** Earlier
notes showed `line.Tokens` / `line.StateOut`; that would put TextMateSharp types
inside `Model/`, which exists to be testable with nothing but the BCL.
`TextMateHighlighter` keeps a `List<LineState>` index-aligned to buffer rows and
splices it in response to `TextBuffer.LinesChanged` — `(row, removed, inserted)`.
Splicing rather than discarding is the point: the rows below an edit keep their
cached state, and comparing against it is what lets the walk stop.

The event lives on `TextBuffer` rather than `EditorCommands` because undo and redo
mutate the buffer directly and would otherwise bypass it.

**Always pass a real timeout**, not `TimeSpan.MaxValue`. Oniguruma will backtrack
forever on a minified JavaScript line. VS Code does the same thing for the same
reason. Start at 50 ms.

**Never tokenize on the render path.** First paint is uncolored. `Editor.Draw`
brings the visible window up to date before `EditorView.Render`; the painter itself
never runs a regex.

**The rest of the file fills in on the loop thread, not a worker.** Planning notes
said background thread plus a concurrent queue. Marshalling *results* back is easy;
the problem is the input side — a worker would read `TextBuffer` while the loop is
editing it, and `Model/` is not thread-safe. Instead each frame spends a fixed
1.5 ms budget walking further down the file. Same user-visible behaviour, no race.
`IHighlighter.Pump()` is kept on the interface (a no-op today) so a future engine
can background its work without the loop changing.

That budget is the whole per-keystroke cost: repairing an edit converges in a line
or two, and everything else in the frame goes to lookahead. Raise it and the
ROADMAP's 5 ms edit budget goes with it.

### Grammars

21 files, ~1.2 MB raw, ~134 KB gzipped, embedded as resources. Sources and scope
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

Markdown references 61 external scopes for fenced code blocks. The 16 languages
we ship will highlight inside fences; the rest render as plain text. That is
correct degradation, not a bug.

TypeScript is two grammars, not one. `source.ts` and `source.tsx` are separate
files upstream — VS Code generates JavaScript's grammar *from* TypeScript's rather
than the other way round — so `.tsx` needs its own vendored file and gets nothing
from `source.ts`. Both went through `RawGrammarFixup` and `GrammarStore` clean on
the first try, which is not the norm for this set.

### TextMateSharp landmines

Three things that each looked like a different bug than they were. All three are
covered by `GrammarStoreTests`, which loads every grammar *and makes it tokenize* —
rule compilation is lazy, so loading alone proves nothing.

**Inflate the resource fully before parsing.** `GrammarReader.ReadGrammarSync`
does not survive a non-seekable stream. Handing it a `StreamReader` over a live
`GZipStream` silently loses characters on the larger grammars — Python and
JavaScript came back null, and XML and YAML failed later as a cast error deep in
rule compilation. `GrammarStore.OpenResource` copies to a `MemoryStream` first.

**Some VS Code grammars are malformed in ways VS Code tolerates.** All three YAML
variants park a `"comment"` string inside a `captures` map, and XML's JSP comment
rule has `"end"` and `"name"` nested inside `captures` where they belong beside it,
plus a sibling rule with a `begin` and no `end`. VS Code only reads numbered
capture keys and treats an end-less pattern as a match rule; TextMateSharp casts
every capture value to `IRawRule` and builds a begin/end rule with a null pattern.
`RawGrammarFixup` repairs both on load. Markdown is collateral damage — compiling
it resolves its fenced-code include of `text.xml`.

The fix is at load time, not in `fetch-grammars.ps1`, so the vendored `.gz` files
stay byte-identical to upstream and a re-fetch diff shows real upstream change.

**`StateStack.Equals` is not structural.** It returns false for two stacks
identical in depth, rule id, end rule and scope path. This hides for most grammars:
when a line leaves the state untouched TextMateSharp returns the *same* object, so
reference equality happens to hold and JSON, INI, TOML, PowerShell and Python
converge after two lines. YAML rebuilds its stack every line, so it never converged
and every keystroke re-tokenized the entire visible window. `StateEquivalence`
compares the chain properly — rule identity and scope paths, deliberately not the
enter/anchor positions, which legitimately differ between equivalent states.

Given YAML is most of what this editor is for, do not "simplify" that back to
`Equals`.

### Native dependency

TextMateSharp wraps Oniguruma through the `Onigwrap` native library. Consequences:

- `IncludeNativeLibrariesForSelfExtract=true` is required for single-file
  publish. First launch extracts to `%TEMP%\.net\nib\<hash>`.
- There was a [historical heap-corruption report](https://github.com/dotnet/runtime/issues/65443)
  combining Onigwrap with `PublishSingleFile`. **It does not reproduce**
  (2026-07-27): `nib --soak` over a published single-file build did 2.3 M lines and
  18.5 M tokens across 20 passes, plus 40 process launches, with zero errors and a
  flat 87 MB working set. Re-run `nib --soak <dir> <passes>` after any TextMateSharp
  or .NET upgrade rather than assuming it stays fixed.

The highlighter is behind `IHighlighter` anyway, so the engine stays swappable if
that ever changes.

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
- **Phases 2–5 implemented and unit-tested (158 tests).** Phase 5 landed
  2026-07-27; the interactive acceptance for phases 3–5 still wants a hardware run.
- Phase 6: see `ROADMAP.md` (note: at the repo root, not `docs/`).

### Corrections carried forward

Early planning said to enable `ENABLE_VIRTUAL_TERMINAL_INPUT` on stdin *and* use
`ReadConsoleInputW`. Those are contradictory. The INPUT_RECORD path is the right
one, so VT input mode is off. Noted here because the wrong version may appear in
older notes.

Startup is **~135 ms**, not the sub-100 ms the ROADMAP asks for, and that is .NET
runtime start: a pre-phase-5 build measures 138 ms on the same machine and phase 5
measures 135. Highlighting adds nothing to it — grammars load lazily, per scope,
after the console is up. Getting under 100 ms means NativeAOT, which the Onigwrap
dependency currently rules out. Don't go looking for it in the highlighter.
