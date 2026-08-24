# Senior Review: nib — 2026-08-23

**Scope:** whole repo at `cd936d8` (v0.6.2). **Assumed bar:** a tool you and a few
machines depend on daily, distributed as a zip with an installer — so "it works on
my box" is not the standard, but "public product with strangers filing issues" is
not either. Nothing skipped.

**Method:** read every source file, built clean, ran the 350-test suite (all pass),
and built a throwaway harness against `Nib.dll` in the scratchpad to reproduce one
rendering finding. No repo files were modified.

**What I couldn't determine** is listed at the end of `00-map.md` — chiefly that I
reviewed statically and did not drive the editor by hand.

---

## Verdict

Yes, I'd own this. It is better documented than most funded projects, the layering
rule (`Model/` never touches `Terminal/`) is real and holds everywhere I checked,
and the comments explain *why* with a consistency I almost never see. The thing I'd
fix first is a one-character omission: tab characters are painted without the
line-number gutter offset, so turning on `-l` and opening any file with a leading
tab erases the line number and shifts selection highlighting left. It is reproduced
below. Everything else is smaller than that or is infrastructure the project has
grown past — mainly the absence of CI for a 350-test suite.

---

## Scorecard

| Dimension | Grade | Reason |
|---|---|---|
| Old code / dead weight | **A** | No commented-out blocks, no stale TODOs, no dead branches. `Probe.cs` is a documented phase-1 diagnostic behind `--probe`, not a leftover. |
| Bad habits | **A−** | No swallowed exceptions without a comment justifying it, no `Console.Write` in the render path, ordinal comparison throughout. One silent-ignore path on CLI arg validation. |
| Scale / runtime | **B** | The incremental tokenizer is genuinely good. One unbudgeted walk can hitch on a far jump; one loop can spin if the console handle dies. |
| Modularity | **B+** | Layering is excellent. `Program.cs` at 863 lines is the one place it isn't — `Editor` is the god object. |
| Data layer | **A−** | Atomic write, byte-exact round-trip, encoding and per-line endings preserved. Missing: an fsync and a staleness check. |
| Framework fit | **A** | TextMateSharp used exactly as intended, with the three known landmines documented and worked around at load time rather than in the vendoring script. |
| Ship-readiness | **B−** | README, `--help`, `--version`, LICENSE, installer, release script all present and correct. No CI, no lockfile, no CHANGELOG, no tags. |
| Light security | **A** | No secrets in tree or history. No vulnerable packages. Nothing network-facing. Registry handling in `install.ps1` is more careful than most installers. |

---

## Findings

### Critical

None. No data-loss path, no security hole, no wrong results in the hot path.

### High

**[H-1] Tab characters are painted without the line-number gutter offset** · confirmed · `src/Nib/UI/EditorView.cs:274`

The glyph branch writes to `_screen.Set(gutter + sx, ...)` (line 281). The tab
branch writes to `_screen.Set(sx, ...)` — the `gutter +` is missing.

**Problem.** With the gutter on, a tab's expanded blank cells land `GutterWidth`
columns to the left of where the tab actually is, painting over the line number.
The following non-tab glyphs are still positioned correctly, so the text looks
right and only the chrome is wrong — which is why it survived review.

**Reproduced** (scratchpad harness, 40-col screen, one line `"\tindented"`, gutter on):

```
GutterWidth = 2  TabWidth = 8
actual   = [          indented    ]
expected = [1       indented      ]
```

The `1` is gone. It was overwritten by the tab's spaces at screen columns 0–7
instead of 2–9.

**Impact.** Any file with leading tabs — Makefiles, tab-indented YAML, most shell
scripts — loses its line numbers on every affected row when `-l` or
`line_numbers = true` is set. Worse, when a selection covers a tab, the selection
background paints `GutterWidth` columns left of the actual tab, so the highlight
misrepresents what is selected. That is a correctness problem, not a cosmetic one:
the user is about to press Ctrl+X against what they think they see.

**Fix.** One character: `_screen.Set(gutter + sx, y, ' ', Color.Default, bg)`.
Add a `LineNumberGutterTests` case with a tab in it — the existing eight cases use
only `"alpha"` / `"x"` / digits, which is exactly why nothing caught this.

---

**[H-2] Mouse capture is on by default, and every event is discarded** · confirmed · `src/Nib/Program.cs:333`, `src/Nib/Model/Config.cs:32`

`Config.Mouse` defaults to `true`, so `ConsoleHost.Acquire` clears
`ENABLE_QUICK_EDIT_MODE` and sets `ENABLE_MOUSE_INPUT`. The loop then does:

```csharp
if (ev.Kind == InputEventKind.Mouse) continue; // phase 6
```

**Problem.** The default costs the user the terminal's own drag-select-and-copy and
returns nothing for it — mouse handling is the *next* roadmap item, not a shipped
one. Two consequences beyond the lost capability:

1. `ENABLE_MOUSE_INPUT` generates a record for every mouse *move* over the window. `ReadBatch` counts those as added events, so `Run` reaches `_view.Message = ""` — **moving the mouse across the terminal wipes "Wrote 42 lines" and any error message off the screen**, and forces a full `Draw()` per motion record.
2. That is wasted redraw work on every idle mouse jiggle.

**Impact.** Every user on every machine is paying for a feature that does nothing,
and status messages vanish for reasons that look random.

**Fix.** Flip the default to `Mouse = false` until the mouse handler lands — it is a
one-word change and the config key and `--mouse` flag already exist to opt back in.
If you'd rather keep the capture, skip the `Message` clear when the batch contained
no key events.

---

**[H-3] A save silently clobbers a file changed on disk since it was loaded** · confirmed · `src/Nib/Model/FileIo.cs:34`

Nothing in `Model/` or `Program.cs` records or checks a last-write time — I grepped
for `LastWrite`/`GetLastWriteTime`/`FileInfo` and there is nothing.

**Problem.** `Save` writes the in-memory buffer over whatever is at the path now.
nano warns ("File was modified since you opened it"); vim refuses. Nib does neither.

**Impact.** This is the one real data-loss path in the project, and the usage
pattern makes it plausible rather than theoretical: a config editor left open in a
tab while something else — a sync client, a deploy script, another machine — rewrites
the file underneath it. This repo lives in a Dropbox folder, which is exactly the
shape of the problem.

**Fix.** Stash `File.GetLastWriteTimeUtc(path)` on `TextBuffer` at load and after
each save; in `DoSave`, compare before writing and route a mismatch through the
existing `Confirm` ("File changed on disk. Overwrite?"). The modal machinery is
already there — this is ~15 lines and no new concepts.

---

### Medium

**[M-1] A far jump tokenizes every intervening line synchronously, unbudgeted** · confirmed · `src/Nib/Highlight/TextMateHighlighter.cs:193`

```csharp
Walk(windowEnd, budget: null);            // no budget
Walk(_states.Count, budget: LookaheadBudget);
```

The first walk is bounded by `windowEnd`, but `Walk` starts at
`min(_dirty, _frontier)` — so the work is proportional to the *distance from the
frontier*, not to the window size. `Ctrl+End` or `^G 20000` on a large file while
the frontier sits near the top tokenizes every line in between in one frame.

**Impact.** A visible hang on a jump. Bounded by file size, so on the few-hundred-line
config files this targets it is invisible; on the log file `nib +200 app.log` in the
README invites, it isn't.

**Fix.** Either cap the unbudgeted walk (tokenize the window from a fresh null state,
accepting one frame of slightly-wrong colour for lines inside a multi-line construct),
or give it a larger budget than the lookahead rather than none. The current comment
says the window "must be right before the frame is painted" — that is a defensible
choice, but the cost is unbounded and the comment doesn't say so.

---

**[M-2] The build is not reproducible: no lockfile, floating package versions** · confirmed · `tests/Nib.Tests/Nib.Tests.csproj:22-24`

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
<PackageReference Include="xunit" Version="2.*" />
<PackageReference Include="xunit.runner.visualstudio" Version="3.*" />
```

No `packages.lock.json` in either project.

**Impact.** A clean clone six months from now resolves different packages than the
one that produced 0.6.2. For a project whose release script gates the zip on the
test suite passing, the suite's own dependencies drifting silently is the wrong
thing to leave loose. `TextMateSharp` is correctly pinned to `1.0.66` — the tests
should get the same treatment.

**Fix.** Pin the three explicitly and add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
to both csprojs; commit the lock files. Ten minutes.

---

**[M-3] No CI** · confirmed · no `.github/` directory

350 tests that only run when someone remembers, plus `ScriptEncodingTests` guarding
a PowerShell landmine that CLAUDE.md says cost real time to find once.

**Impact.** `release.ps1` does run the suite before zipping, so a regression can't
reach a release — that's the important half and it's covered. What's missing is the
fast half: catching a break at push time rather than at release time.

**Fix.** One `windows-latest` workflow: `dotnet build` + `dotnet test`. Fifteen
lines, and it makes the badge in the README honest if you ever add one.

---

**[M-4] The input loop can spin at 100% CPU if `ReadConsoleInputW` fails** · likely · `src/Nib/Terminal/InputReader.cs:271`, and five call sites

`ReadBatch` returns 0 both when a batch decoded to nothing (normal, and the next
call blocks) and when the native read *failed* (not normal, and the next call fails
immediately too). Every caller treats them the same:

```csharp
if (_reader.ReadBatch(_batch) == 0) continue;
```

— in `Run`, `RunPrompt`, `Confirm`, `ConfirmReplace`, `ShowHelp`.

**Impact.** If the console handle goes bad — window closed, console detached — the
loop busy-spins instead of blocking. In practice `CTRL_CLOSE_EVENT` gives Windows
~5 seconds before it kills the process, so the worst case is a short CPU burn during
teardown rather than a hang. Marked `likely` because I did not force a failing read;
what I confirmed is that the two cases are indistinguishable to every caller.

**Fix.** Have `ReadBatch` return `-1` (or take an `out bool ok`) on native failure
and let the loops exit. `TerminalWriter.Flush` already handles the mirror-image case
correctly at line 244 — this is the read side of the same idea.

---

**[M-5] Bad `--tab-width` and `--theme` values are silently ignored** · confirmed · `src/Nib/Program.cs:143-147`

```csharp
case "--tab-width":
    if (i + 1 < args.Length && int.TryParse(args[++i], out int w)
        && w >= Config.MinTabWidth && w <= Config.MaxTabWidth)
        result.TabWidth = w;
    break;   // no else — a bad value vanishes
```

Same for `--theme` with a missing or unknown value, and for any unrecognised
`-flag` (the `default` branch drops it).

**Impact.** Inconsistent with `config.toml`, which reports *every* problem line on
the message row and does so well — `tab_width = 0` in the file is a reported error,
`--tab-width 0` on the command line is silence. `nib --tab-width foo.conf` also eats
the filename as the flag's value and opens an empty unnamed buffer, which reads as
"nib lost my file". A typo'd long flag does nothing with no feedback.

**Fix.** Collect CLI problems into the same `openingMessage`/`MessageKind.Error`
channel `Config.DescribeProblems` already feeds. The plumbing exists; the arg parser
just isn't using it.

---

**[M-6] `Save` does not flush to disk before `File.Replace`** · confirmed · `src/Nib/Model/FileIo.cs:56`

`File.WriteAllBytes(temp, output)` closes the handle but does not force the data out
of the OS cache. `File.Replace` then makes the temp file the real file.

**Impact.** The atomic-rename design protects against a *failed or interrupted write*,
which is the common case and is handled correctly. It does not protect against power
loss between the write and the rename, where the rename can land with the file's
contents still unflushed — the one outcome the comment at line 30 says can't happen
("never a truncated config"). Low likelihood, but the comment overstates the guarantee.

**Fix.** Write through a `FileStream` and call `.Flush(flushToDisk: true)` before the
replace, or soften the comment. I'd do the former — it's three lines and the claim is
worth keeping true.

---

**[M-7] `Program.cs` is 863 lines and `Editor` is a god object** · confirmed · `src/Nib/Program.cs`

`Main`, `Parse`, `PrintHelp`, `ParsedArgs`, and the entire `Editor` class — loop,
save, quit, go-to-line, find, replace, stats, three modal input loops, and the help
overlay — all in one file. It is the only place in the repo where the otherwise
excellent layering doesn't hold.

**Impact.** Nothing in `Editor` is testable; `MessageRowTests` and friends reach
`EditorView` instead. The search/replace logic in `DoReplace` (lines 528–608) is the
most intricate control flow in the project and has no unit test — only `ReplaceScope`,
the part correctly extracted into `Model/`, does.

**Fix.** Split `Editor` out of `Program.cs` and lift the modal loops (`RunPrompt`,
`Confirm`, `ConfirmReplace`) behind a small interface the tests can fake. That would
make `DoReplace`'s walk testable, which is where the remaining edge cases live. This
is the largest item here and the one I'd schedule rather than squeeze in.

---

### Low

**[L-1] Extra positional arguments are silently dropped** · confirmed · `src/Nib/Program.cs:159` — `result.File ??= a` keeps the first and `Positional` collects the rest, but only `--soak` reads the list. `nib a.conf b.conf` opens `a.conf` and says nothing about `b.conf`. One line in the same error channel as M-5.

**[L-2] Vertical movement at the buffer edges destroys the desired column** · confirmed · `src/Nib/Model/Cursor.cs:208,215` — `Up()` at row 0 and `Down()` at the last row both call `RememberColumn()` after snapping to column 0 / end-of-line. Hold Down through the last line and the column memory the class exists to preserve is gone. Also fires mid-`PageUp`/`PageDown`, since those loop `Up()`/`Down()`.

**[L-3] A clipped line loses its trailing selection cell** · confirmed · `src/Nib/UI/EditorView.cs:286` — the `return` on running off the right edge skips the "selection includes the line break" cell at line 291. Only visible when a selection spans a line longer than the window; cosmetic.

**[L-4] Folder is `UI/`, docs and namespace say `Ui`** · confirmed · `src/Nib/UI/`, namespace `Nib.Ui`, and `Ui/` in CLAUDE.md (3 refs) and ARCHITECTURE.md (3 refs). Harmless on Windows, a broken path on a case-sensitive filesystem, and a failed grep for the next reader. Rename the folder to `Ui` to match everything else.

---

## Things done well

Specifically, and these are not padding:

- **The comments.** `Terminal/ConsoleHost.cs`, `Model/FileIo.cs` and `Commands/EditorCommands.cs:410` explain the failure that motivated the code, not what the code does. The `ApplyReplace` comment traces an actual crash — stale anchor surviving a line join, then Ctrl+C indexing off the end — from symptom to cause to fix. That is the comment the next maintainer needs and almost nobody writes.
- **`Model/` never references `Terminal/`.** I checked; it holds. That is why undo, selection, search, replace-scope arithmetic and the byte-exact round-trip all have real unit tests, and it is the single best structural decision in the project.
- **The incremental tokenizer's convergence rule** (`Highlight/TextMateHighlighter.cs:236`) with `StateEquivalence` rather than `StateStack.Equals`. Finding that YAML rebuilds its stack every line — and that reference equality was masking it for every other grammar — is the kind of thing that only comes from measuring.
- **`RawGrammarFixup`** repairing malformed upstream grammars at *load* time rather than in the vendoring script, so the committed `.gz` files stay byte-identical to upstream and a re-fetch diff shows real change. That is a deliberate cost paid for a maintenance property, and the reasoning is written down.
- **`install.ps1`'s PATH handling.** Reading with `DoNotExpandEnvironmentNames`, writing with the value kind preserved, broadcasting `WM_SETTINGCHANGE` via `SendMessageTimeout` rather than `SendMessage`. Most installers get at least one of those three wrong and corrupt someone's PATH.
- **`ScriptEncodingTests`** turning a debugging war story (a UTF-8 em dash making `Add-ToUserPath` execute `Remove-FromUserPath` under CP-1252) into a test. Verified: all three scripts are pure ASCII, and `release.ps1` runs the suite before it zips.
- **The `?` on every settings field of `ParsedArgs`**, which is what makes "flag beats config beats default" actually work. Easy to tidy away, correctly commented against it.
- **`release.ps1` verifies `-p:Version` reached the assembly** by running the staged exe and comparing `--version` output. A real smoke test, not a checkbox.

---

## Recommended order of work

1. **[H-1] Add `gutter +` to the tab branch, and a tab case to `LineNumberGutterTests`.** ≤1 hour. One character plus one test.
2. **[H-2] Default `Mouse` to `false` until the mouse handler lands.** ≤1 hour. Removes a live regression for every user.
3. **[L-4] Rename `src/Nib/UI/` → `src/Nib/Ui/`.** ≤1 hour. Do it in the same commit as 1 while you're in that file.
4. **[M-2] Pin the test packages and commit lock files.** ≤1 hour.
5. **[M-3] Add a `windows-latest` build+test workflow.** ≤1 hour.
6. **[H-3] Stash the load-time mtime and confirm before overwriting a changed file.** ~half a day. The only real data-loss path; the modal `Confirm` already exists.
7. **[M-5 + L-1] Route CLI arg problems through the existing error-message channel.** ~half a day.
8. **[M-6] `Flush(flushToDisk: true)` before `File.Replace`.** ≤1 hour, once you've decided whether you want the guarantee or the softer comment.
9. **[M-4] Distinguish "read failed" from "nothing decoded" in `ReadBatch`.** ~half a day including the five call sites.
10. **[M-7] Extract `Editor` from `Program.cs` and put `DoReplace`'s walk under test.** Schedule this; don't squeeze it in. **[M-1]** is worth revisiting at the same time, since both are about the loop's frame budget.

---

## Open questions for the author

1. **Is `-l` in daily use?** H-1 has presumably been on screen since the gutter landed. If you've been running with it on and never noticed, that tells me something useful about how it reads on a real terminal — and if you've been running with it off, that explains why.
2. **Do you want the staleness check (H-3) at all?** It adds a modal question to the save path, which is exactly the "ceremony" the README says nib exists to avoid. My read is that it earns its place because the alternative is silent loss, but it is a taste call and it's yours.
3. **What's the real target file size?** M-1 is invisible at "a few hundred lines" and unpleasant at "a log file". The README advertises `nib +200 app.log`, which points at the second. If logs are in scope, the frame budget deserves another pass; if they aren't, drop the example.
4. **Is `--soak` still earning its place in the shipped binary?** It's a good diagnostic and CLAUDE.md says to re-run it after every TextMateSharp bump — but it's the only mode that takes a directory argument from a user, and it's dead weight in the release build. Worth a `#if DEBUG` or worth keeping deliberately?
5. **Is 0.6.2 tagged anywhere?** `release.ps1` records the commit in `VERSION.txt`, but there are no git tags. If you ever need to reproduce a zip someone is running, the tag is what you'll wish you had.
