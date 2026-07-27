# Project Progress

## Current Focus
Phase 3 (editing core) implemented and unit-tested (59 tests green). Pending: interactive
hardware acceptance (typing feel, save on a real console, homelab config round trip). Next: phase 4 (selection, clipboard, undo).

## Open Todos
- [x] Phase 1 — terminal foundation (verified 2026-07-26)
- [x] Phase 2 — renderer: Screen diff, viewport/scroll, tab expansion, resize, debug frame timer
- [x] Phase 3 — editing core: TextBuffer, Cursor, FileIo (byte-for-byte round trip covered first), nano chrome, Keymap
- [x] Replaced throwaway `Ui/FileDocument` with `Model/TextBuffer` + `Model/FileIo`
- [ ] Verify phase-3 ROADMAP acceptance on hardware (interactive; the byte round-trip and column-memory checks are unit-tested, the "edit a homelab config" check is not)
- [ ] Decide whether to keep the committed 660 KB `tests/fixtures/sample-10k.txt` or gitignore + generate

## Progress Log

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
