# Project Progress

## Current Focus
Phase 5 (syntax highlighting) implemented and unit-tested (158 tests green). All automatable ROADMAP
acceptance for phase 5 passes; the single criterion not met — sub-100 ms startup — is .NET runtime cost
that predates the phase. Pending: the interactive hardware run for phases 3, 4 and 5. Next: phase 6 (polish).

## Open Todos
- [x] Phase 1 — terminal foundation (verified 2026-07-26)
- [x] Phase 2 — renderer: Screen diff, viewport/scroll, tab expansion, resize, debug frame timer
- [x] Phase 3 — editing core: TextBuffer, Cursor, FileIo (byte-for-byte round trip covered first), nano chrome, Keymap
- [x] Phase 4 — selection, clipboard, undo: unified edit model, coalescing undo/redo, selection, CUA + ^K/^U clipboard
- [x] Phase 5 — syntax highlighting: vendored grammars/themes, lazy GrammarStore, ThemeMap, language detection, convergence-cached incremental tokenization, `--theme` + Alt+T
- [x] Replaced throwaway `Ui/FileDocument` with `Model/TextBuffer` + `Model/FileIo`
- [ ] Verify phase-3 ROADMAP acceptance on hardware (interactive; the byte round-trip and column-memory checks are unit-tested, the "edit a homelab config" check is not)
- [ ] Verify phase-4 ROADMAP acceptance on hardware: (1) Shift/Ctrl+Shift selection paints and collapses; (2) ^C/^X/^V and ^K/^U behave; (3) ^X with no selection still prompts to quit; (4) type-word→^Z→^Y and a long undo-to-original session; (5) paste 500 lines with no visible lag; (6) copy→Notepad/browser and back round-trips endings
- [ ] Verify phase-5 acceptance on hardware: (1) open each of `tests/fixtures/highlight/sample.*` and confirm the colours look right, not merely present; (2) Alt+T cycles all five themes with no artifacts and the selection background tracks the theme; (3) a large YAML file scrolls without visible lag as the lookahead fills it in; (4) colour is correct in legacy `conhost` as well as Windows Terminal, and degrades cleanly if VT processing cannot be set
- [ ] Decide whether to keep the committed 660 KB `tests/fixtures/sample-10k.txt` or gitignore + generate
- [ ] Consider re-running `nib --soak` on any TextMateSharp or .NET upgrade — it is the only thing standing between us and the Onigwrap heap report

## Progress Log

### 2026-07-27 (phase 5)
- Syntax highlighting landed. `Highlight/` (may reference `Terminal/`, never the reverse): `IHighlighter` + `StyledSpan` + `NullHighlighter`; `GrammarStore` (`IRegistryOptions` over gzipped embedded resources, lazy per scope, negative results cached); `GrammarManifest` (the vendoring manifest re-read at runtime as a 25th resource, so detection tables and vendored files can't drift); `ThemeMap` (wraps TextMateSharp's own `Theme.Match` rather than reimplementing selector specificity); `LanguageDetector`; `TextMateHighlighter`.
- Vendored 19 grammars + 5 themes, 777 KB raw → 107 KB gzipped. `grammers/` → `grammars/` and `Fetch-Grammers.ps1` → `fetch-grammars.ps1` (the script's own default `-ManifestPath` had always pointed at the correctly-spelled path, so it could never have run). Two fixes to the script: `Invoke-WebRequest -UseBasicParsing` returns `Content` as a string on PowerShell 5.1 and bytes on 7; and VS Code ships its themes as JSONC, which `ConvertFrom-Json` rejects — added a string-aware comment stripper.
- **Soak test first, per ROADMAP.** `nib --soak <dir> <passes>` against the *published* single-file build: 2,295,460 lines / 18,515,540 tokens over 20 passes, 40 separate process launches, 0 errors, flat 87 MB. The Onigwrap + `PublishSingleFile` heap report (dotnet/runtime#65443) does not reproduce. Kill criterion not triggered; the hand-rolled-tokenizer fallback stays unbuilt.
- Three TextMateSharp incompatibilities found, all of which fail silently or misleadingly (written up in CLAUDE.md):
  1. `GrammarReader` does not survive a non-seekable stream — a `StreamReader` over a live `GZipStream` loses characters on the larger grammars. Python and JavaScript came back null; XML and YAML failed much later as a cast error inside rule compilation. Fix: inflate to a `MemoryStream` first.
  2. Upstream grammars are malformed in ways VS Code tolerates: all three YAML variants put a `"comment"` string inside a `captures` map, and XML's JSP comment rule has `"end"`/`"name"` nested inside `captures` plus a sibling `begin` with no `end`. `RawGrammarFixup` lifts the misplaced keys out and rewrites an end-less `begin` to `match`. Markdown was collateral — compiling it resolves its fenced-code include of `text.xml`. Fixed at load time, so the vendored `.gz` stay byte-identical to upstream.
  3. `StateStack.Equals` is not structural. It hides because a line that leaves the state untouched gets the *same* object back, so reference equality carries JSON/INI/TOML/PowerShell/Python. YAML rebuilds its stack per line and so never converged: every keystroke re-tokenized the whole visible window. `StateEquivalence` compares the chain properly. YAML went from 37 lines per edit to 2.
- `Model/TextBuffer` gained `LinesChanged(row, removed, inserted)` — on the buffer, not the command layer, because undo/redo mutate it directly. The highlighter splices its `List<LineState>` to match rather than discarding it; keeping the rows below an edit is exactly what makes convergence detectable.
- Deviation from plan: **no worker thread.** Visible window is synchronous; the rest fills in on the loop thread under a 1.5 ms per-frame budget. A worker would read `TextBuffer` while the loop edits it — marshalling results back fixes the output side of that race, not the input side. `Pump()` kept as a no-op on the interface.
- `Ui/EditorView.DrawLine` walks spans alongside characters with one cursor (spans are ordered and non-overlapping), so tab expansion and horizontal scroll are untouched; selection background still wins over token colour, and the hardcoded selection blue now defers to the theme's `editor.selectionBackground` where one exists (Dark+ and Light+ declare none — VS Code supplies those from built-ins we don't vendor).
- `--theme <id>` at startup, Alt+T to cycle. Alt rather than Ctrl: every Ctrl chord is spoken for by the nano/CUA bindings.
- Measurements: edit on a 20k-line YAML = 1.7 ms (repair itself is 2 lines); open + colour first screen of 20k lines = 23–44 ms; jump to EOF of an untokenized 20k-line file = 0.4–0.9 s once, then cached (the sequential walk is the tradeoff for not having provisional state); published exe 1.33 MB (was 324 KB), budget 10 MB.
- Startup is **135 ms**, against 138 ms for a pre-phase-5 build measured in a worktree on the same machine. The ROADMAP's sub-100 ms criterion is not met and never was — it is .NET runtime start, not highlighting. Sub-100 ms needs NativeAOT, which Onigwrap precludes.
- Tests: 158 green (was 88). New: `GrammarStoreTests` (every grammar loads *and tokenizes* — compilation is lazy, so loading proves nothing), `ThemeMapTests`, `LanguageDetectorTests`, `HighlightCacheTests` (convergence and cache alignment), `HighlightFixtureTests` (14 committed sample files under `tests/fixtures/highlight/`), `HighlightBudgetTests`. Clean build, warnings-as-errors.
- Next: interactive hardware acceptance for phases 3–5 (see Open Todos), then phase 6.

### 2026-07-27 (phase 4)
- Selection / clipboard / undo landed. `Model/` stays console-free: `TextPosition` (ordered row/col), `Selection` (anchor + live-cursor head, per-row spans for the view), `Model/Undo/Edit` + `Model/Undo/UndoStack`. `TextBuffer` gained the three ranged verbs everything reduces to — `GetRange` / `DeleteRange` / `InsertMultiline` — plus a static `Advance`. `InsertMultiline` preserves the endings embedded in its text (that is what makes undo byte-exact); paste stays consistent by normalizing the clipboard to the buffer's dominant ending first.
- Unified edit model: every mutation is "replace [start,end) with text", recorded as one `Edit`. `EditorCommands.ApplyReplace` is the single choke point; typing and delete runs coalesce (same line, contiguous, 300 ms, injected clock for deterministic tests). Paste/cut/Enter/select-delete force an undo boundary.
- `Commands/`: new `IClipboard` seam (`SystemClipboard` wraps `Terminal.Clipboard`; a fake drives the tests). `EditorCommands` reworked — selection-aware typing/backspace/delete, `Move(extend:)`, Copy/Cut/Paste, SelectAll, `^K`/`^U` line register, Undo/Redo. `Keymap` wires Shift+move, Ctrl+Shift+word, CUA `^C/^X/^V`, `^Z`/`^Y`, `^K`/`^U`, `^A`; `^X` cuts a selection else returns Quit.
- `Ui/`: `EditorView.DrawLine` paints the selection span (fixed blue bg until phase-5 themes; one trailing cell marks a selected line break). `HelpBar` refreshed; `Program` injects `SystemClipboard` and hands the selection to the view.
- Decisions (confirmed with Jeremy): redo = Ctrl+Y; clipboard normalizes CRLF out / dominant-ending in; coalesce both typing and delete runs. `^K` replaces the line register (no nano-style accumulation — not in acceptance).
- Tests: 88 green (was 59). New: `TextBufferRangeTests`, `UndoStackTests` (the undo/redo byte-exact round trip — the phase's most important test), `SelectionTests`, `EditorCommandsTests` (fake clipboard). Clean build, warnings-as-errors.
- Next: run the interactive phase-4 acceptance on hardware (see Open Todos), then phase 5.

### 2026-07-27 (phase 3)
- Editing core landed. `Model/` (console-free by rule): `TabStops` (tab math extracted from `Ui/Viewport`, which now delegates), `Line`/`LineEnding`/`DocumentEncoding`, `TextBuffer` (insert/split/join/delete, per-line ending preservation, dominant-ending choice for new breaks), `Cursor` (display-column desired-column memory), `FileIo` (BOM sniff UTF-8/16/32 → strict UTF-8 → `Encoding.Latin1` fallback; atomic temp-file-then-`File.Replace` save).
- `Ui/`: `EditorView` rebuilt to the nano layout (title row + text + message/prompt row + two help rows, `TextRows = Height-4`), hardware-cursor placement, `StatusBar`, `HelpBar`, `Prompt`. `Viewport.EnsureVisible` scroll-to-cursor. `FileDocument`/`IDocumentSource` retired.
- `Commands/`: `Keymap` + `EditorCommands` — typing, Enter/Backspace/Delete, arrows/Home/End/PgUp/PgDn, Ctrl+arrows by word, Ctrl+S/^O save, Ctrl+X quit-with-modified-prompt, Ctrl+G brief help. Selection/clipboard/undo deliberately deferred to phase 4 (Ctrl+X always quits for now).
- `Program.cs`: `Editor` class with the render/input loop, modal save-as prompt and quit-confirm.
- Encoding decision: Latin1 (not Windows-1252) as the no-BOM fallback — byte-lossless and dependency-free (1252 would need the CodePages package, against the no-deps rule). No effect on the acceptance encodings.
- Tests: 59 green (was 23). New: `FileIoRoundTripTests` (LF/CRLF/mixed/no-trailing/empty/UTF-8-BOM/UTF-16-LE+BE/Latin1 all byte-identical), `TextBufferTests`, `CursorTests`, `TabStopsTests`, `ViewportScrollTests`. Clean build (warnings-as-errors); Release single-file publish 285 KB.
- Next: run the interactive ROADMAP phase-3 acceptance on hardware, then phase 4.

### 2026-07-27
- Phase 2 renderer shipped: `Terminal/Screen` (front/back diff, cross-frame SGR persistence so idle frames emit nothing), `Ui/Viewport` (pure tab-stop + char↔display-column math), `Ui/EditorView`, throwaway `Ui/FileDocument`.
- Probe moved to `nib --probe`; `nib <file>` is the read-only viewer; `--debug`/`NIB_DEBUG` shows per-frame ms.
- Stood up `tests/Nib.Tests` (xUnit) — 23 tests green; build clean warnings-as-errors; Release single-file publish 250 KB.
- All 5 ROADMAP phase-2 acceptance checks pass on hardware; committed `tests/fixtures/sample-10k.txt`.
- Deliberate: non-tab control chars render as single `?` (keeps tab-only column math exact; caret notation deferred).
- Next: phase 3 editing core — write the byte-for-byte round-trip test before any edit op exists.
