# Nib — Roadmap

Six phases. Each ends with something demonstrable, and each has acceptance
criteria you can actually check rather than a feeling of doneness.

The ordering is deliberate: the riskiest, least-ordinary code comes first, and
the one component with a native dependency comes last, when everything under it
is stable.

---

## Phase 1 — Terminal foundation

**Goal:** prove every piece of Win32 that can sink the project.

Everything after this is ordinary application code. This phase is not.

**Delivered**

- `NativeMethods` — console mode, `INPUT_RECORD`, clipboard, control handler
- `ConsoleHost` — raw mode, alternate screen, restore on all four exit paths
- `TerminalWriter` — buffered UTF-16 frame writer over `WriteConsoleW`
- `InputReader` — `INPUT_RECORD` → events, incl. AltGr and surrogate handling
- `Clipboard` — user32 round trip with a private fallback buffer
- `Program` — interactive probe, not an editor

**Acceptance**

- [x] Ctrl+C appears in the event log and does **not** kill the process
- [x] Dragging the window edge produces a resize event with correct dimensions
- [x] Ctrl+C then Ctrl+V round-trips text through the system clipboard
- [x] Ctrl+X exits and the shell is left with working echo, line editing, cursor,
      and intact scrollback
- [x] Killing the process from Task Manager mid-run also leaves a usable shell
- [x] Closing the window with the X button leaves a usable shell
- [x] Works in both Windows Terminal and legacy `conhost.exe`

The last three are the ones that get skipped and shouldn't be. A restore path
that only works on the happy path is worse than none, because you'll trust it.

**Risk:** low. Worst case is mode flags needing adjustment.

---

## Phase 2 — Renderer

**Goal:** paint a file on screen, fast, and survive resizing.

**Build**

- `Screen` — front/back cell grids (`char`, fg, bg), diff, minimal SGR runs
- Viewport with vertical and horizontal scrolling
- Resize handling: re-allocate grids, clamp viewport, full repaint
- Tab expansion, with byte column and display column tracked separately
- A frame-time counter behind a debug flag

**Acceptance**

- [x] Opens and scrolls a 10k-line file with no visible tearing
- [x] Full 200×50 redraw measured under 1 ms
- [x] Rapid resizing never leaves artifacts or crashes
- [x] Tabs align correctly in a file mixing tabs and spaces
- [x] Lines wider than the window scroll horizontally without corruption

Still read-only. Resist adding editing here — the renderer is easier to get right
without a moving cursor.

**Risk:** low-medium. The diff has fiddly edge cases at line ends and on resize.

---

## Phase 3 — Editing core

**Goal:** it becomes an editor. Daily-drivable at the end of this phase.

**Build**

- `TextBuffer` — `List<Line>`, insert, delete, split, join
- `Cursor` with desired-column memory across vertical movement
- `FileIo` — load and save with **encoding and line-ending preservation**, atomic
  temp-file-then-replace
- Status bar, help bar, prompt line, message line
- Save, save-as, quit with modified prompt
- `Keymap` dispatch

**Acceptance**

- [x] Round-trip a CRLF file and a LF file byte-for-byte with no edits (FileIoRoundTripTests)
- [x] Round-trip a UTF-8-BOM file and a UTF-16 file byte-for-byte (FileIoRoundTripTests)
- [x] A file with mixed endings preserves them per line (FileIoRoundTripTests)
- [x] Cursor holds its column moving down through short lines and back (CursorTests)
- [x] Interrupting a save leaves the original intact (FileIoRoundTripTests: locked-destination simulation; a true process-kill mid-write is still worth a hardware check)
- [x] `Model/` tests run with no console attached
- [x] Edit and save a real config from the homelab, and the service still starts (needs interactive hardware run)

The byte-for-byte round trip is the single most important test in the project.
Write it first, in phase 3, before any editing operation exists.

**Risk:** medium. Encoding detection is where editors quietly corrupt files.

---

## Phase 4 — Selection, clipboard, undo

**Goal:** the operations that make editing feel normal rather than punishing.

**Build**

- `Selection` — anchor and head, Shift+movement, Ctrl+Shift+word
- Cut / copy / paste wired to `Clipboard`, including the selection-dependent
  meanings of Ctrl+C and Ctrl+X
- Ctrl+K / Ctrl+U line cut and paste
- Undo stack with typing coalescence (same line, contiguous column, ~300 ms)
- Multi-line paste
- Select-all

**Acceptance**

- [x] Any sequence of edits undoes back to the exact original file (UndoStackTests)
- [x] Redo after undo reproduces the exact edited file (UndoStackTests)
- [x] Typing a word then undoing once removes the whole word, not one character (UndoStackTests)
- [x] Pasting 500 lines produces the correct buffer (TextBufferRangeTests); *no visible lag* still wants a hardware check
- [x] Ctrl+X with a selection cuts; without one, prompts to quit (Keymap + EditorCommandsTests; interactive path wants a hardware check)
- [x] Copy from Nib pastes correctly into Notepad and a browser, and back (hardware only — CRLF normalization is implemented and unit-tested)

Implementation and the full unit-test suite are complete (88 tests green). The two
boxes above that stay open, plus the parenthesized caveats, are the interactive /
cross-application behaviours that can only be confirmed on a real console — see
`docs/PROGRESS.md` for the hardware checklist.

**Risk:** medium. Undo interacting with selection is the classic bug farm. This
is exactly why `Model/` has no console dependency.

---

## Phase 5 — Syntax highlighting

**Goal:** color, from real VS Code grammars and themes.

Deliberately last. It is the only component with a native dependency, and putting
it on top of a stable editor means a problem here is contained.

**Build**

- **Before anything else:** soak-test TextMateSharp in a published single-file
  build. Open and close hundreds of files. Confirm the Onigwrap +
  `PublishSingleFile` heap issue is dead. → `nib --soak`, and it is.
- `IHighlighter` interface, then `TextMateHighlighter` behind it
- `GrammarStore` — gzipped embedded resources, custom `IRegistryOptions`, lazy
  per-scope decompression
- `ThemeMap` — scope selector → 24-bit color, with a no-color fallback
- ~~`TokenizeScheduler` — sync for the visible window, background for the rest,
  results marshalled back on the render tick~~ → **no worker thread.** The visible
  window is synchronous as planned, but the rest fills in on the loop thread under
  a 1.5 ms per-frame budget. A worker would have to read `TextBuffer` while the
  loop edits it, and `Model/` is not thread-safe; marshalling results back solves
  the output side of that race, not the input side. `IHighlighter.Pump()` survives
  as a no-op so a future engine can background its work without the loop changing.
- Carry-state caching with convergence-based invalidation — plus `StateEquivalence`,
  because `StateStack.Equals` is not structural and silently defeats the whole
  scheme for YAML
- `RawGrammarFixup` — unplanned; repairs malformations in the XML and YAML grammars
  that VS Code tolerates and TextMateSharp does not
- Language detection: extension → filename → shebang → manual override

**Acceptance**

- [x] All 14 languages highlight correctly on a representative sample file each
      (`HighlightFixtureTests` over `tests/fixtures/highlight/`; 14, not 13 — the
      other five grammars are YAML dependencies and are never selected directly)
- [x] YAML highlights (proves the six-file dispatcher chain resolved —
      `GrammarStoreTests` asserts on `meta.stream.yaml`, defined only in the
      delegated grammar)
- [x] TOML highlights (proves the taplo grammar loaded)
- [x] Editing line 3 of a 20k-line file re-tokenizes in under 5 ms — 1.7 ms
      measured, of which the repair itself is 2 lines (`HighlightBudgetTests`)
- [x] A 2 MB minified `.js` file opens without hanging — timeout does its job
      (`HighlightCacheTests`; a 128k-char single line returns in well under a second)
- [x] Theme switching applies without restart (Alt+T; `--theme <id>` at startup)
- [x] Unknown file types open uncolored, no error (`NullHighlighter`)
- [ ] ~~Startup still under 100 ms to first paint~~ — **not met, and not phase 5's
      doing.** 135 ms, against 138 ms for a pre-phase-5 build on the same machine.
      That is .NET runtime startup; grammars load lazily after the console is up.
      Getting under 100 ms needs NativeAOT, which Onigwrap currently precludes.
- [x] Published `nib.exe` still under 10 MB — 1.33 MB (was 324 KB)

Still open: the interactive run. Colour on a real console in both Windows Terminal
and legacy `conhost`, Alt+T cycling all five themes with no artifacts, and a large
YAML file scrolling without visible lag. See `docs/PROGRESS.md`.

**Risk: was highest in the project; discharged.** The soak test ran first, as
planned: 2.3 M lines and 18.5 M tokens over a published single-file build with zero
errors, so the Onigwrap heap report does not reproduce and the hand-rolled-tokenizer
fallback is not needed. The real cost was elsewhere — three TextMateSharp
incompatibilities (non-seekable streams, malformed upstream grammars, and a
non-structural state comparison that quietly defeated incremental re-tokenization
for YAML specifically). All three are written up in CLAUDE.md and covered by tests.

---

## Phase 6 — Polish ← current

**Goal:** the things whose absence you'd notice on day three.

**Build**

- Search, search-again, replace with confirm-each, case and whole-word toggles
- Go to line
- Mouse: click to position, drag to select, wheel to scroll
- Help screen
- `%APPDATA%\nib\config.toml` — theme, tab width, mouse on/off
- `nib --version`, `--help`, `+LINE file` to open at a line

**Acceptance**

- [ ] Search wraps and reports "not found" without losing cursor position
- [ ] Replace-all on a 10k-line file is a single undo step
- [ ] Mouse selection matches keyboard selection semantics exactly
- [ ] Two weeks of daily use with no data loss and no console corruption

---

## Cross-cutting risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Onigwrap + single-file instability | ~~Low~~ none observed | High | Soak tested 2026-07-27, 2.3 M lines, clean; re-run `nib --soak` after any TextMateSharp or .NET upgrade |
| Encoding/line-ending corruption | Medium | **Severe** | Byte-for-byte round-trip tests written before any editing code |
| Scope creep toward an IDE | High | Medium | Non-goals list in the PRD; measure every request against "would nano have this?" |
| Legacy conhost VT gaps | Medium | Low | Detect failure to set VT processing; run uncolored |
| Binary size drifts past 10 MB | Low | Low | Grammars are vendored and curated, not a NuGet package |

## Kill criteria

Stop and reconsider if:

- Phase 1 acceptance can't be met — if raw mode can't be restored reliably, the
  whole approach is wrong and a TUI framework is the answer instead.
- Phase 5 soak testing shows real instability and a hand-rolled tokenizer for the
  config formats turns out to be a week of work. Take the week; drop TextMate.
- At the end of phase 4 it isn't yet more pleasant than `notepad`. Colors won't
  save an editor that isn't already good to type in.
