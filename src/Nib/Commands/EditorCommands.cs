using System.Text;
using Nib.Model;
using Nib.Model.Undo;

namespace Nib.Commands;

/// <summary>
/// How a search ended. The command layer reports the outcome and never formats a
/// sentence about it — the message row's wording is the loop's business.
/// </summary>
public enum SearchOutcome
{
    /// <summary>Found ahead of (or behind) the caret without running off the end.</summary>
    Found,

    /// <summary>Found, but only after running off the end and starting again.</summary>
    FoundWrapped,

    /// <summary>The term does not occur. The caret has not moved.</summary>
    NotFound,

    /// <summary>Nothing to search for — F3 with no earlier ^F, or an empty term.</summary>
    NoQuery,
}

/// <summary>
/// The verbs the keymap invokes: each pairs a buffer mutation with the matching
/// cursor move, so the two never drift apart. As of phase 4 it also owns the
/// selection, the undo history, and the clipboard, and every mutation funnels
/// through one <see cref="ApplyReplace"/> primitive — replace [start, end) with
/// text — so undo stays correct no matter which verb ran.
/// </summary>
public sealed class EditorCommands
{
    private readonly TextBuffer _buffer;
    private readonly IClipboard _clipboard;
    private readonly UndoStack _undo;
    private readonly Selection _selection = new();

    // nano's ^K/^U line register: the last line(s) cut, replayed by ^U.
    private string _lineRegister = "";

    public Cursor Cursor { get; }

    /// <summary>The live selection, for the view to paint.</summary>
    public Selection Selection => _selection;

    /// <summary>True when a non-empty selection exists (drives Ctrl+X = cut vs. quit).</summary>
    public bool HasSelection => _selection.IsActive(Cursor);

    public EditorCommands(TextBuffer buffer, Cursor cursor, IClipboard clipboard, UndoStack? undo = null)
    {
        _buffer = buffer;
        Cursor = cursor;
        _clipboard = clipboard;
        _undo = undo ?? new UndoStack();
    }

    private TextPosition Caret => new(Cursor.Row, Cursor.Col);

    /// <summary>
    /// The selection as buffer coordinates, clamped to what the buffer actually has.
    ///
    /// <see cref="Selection"/> stores a raw anchor position and the buffer can shrink
    /// underneath it, so a stale anchor can name a row that no longer exists. Every
    /// use of the selection as an index goes through here rather than through
    /// <see cref="Selection.Range"/> directly — the clear in <see cref="ApplyReplace"/>
    /// is what should prevent a stale anchor, and this is what stops it being a crash
    /// if one ever gets through again.
    /// </summary>
    private (TextPosition Start, TextPosition End) SelectionRange()
    {
        (TextPosition start, TextPosition end) = _selection.Range(Cursor);
        return (Clamp(start), Clamp(end));
    }

    private TextPosition Clamp(TextPosition position)
    {
        int row = Math.Clamp(position.Row, 0, _buffer.LineCount - 1);
        return new TextPosition(row, Math.Clamp(position.Col, 0, _buffer.LineLength(row)));
    }

    // ---- text entry ---------------------------------------------------------

    public void InsertChar(char ch) => TypeText(ch.ToString());

    /// <summary>Insert text with no newline — a typed char or an astral surrogate pair (2 UTF-16 units).</summary>
    public void Insert(string text) => TypeText(text);

    private void TypeText(string text)
    {
        if (_selection.IsActive(Cursor))
        {
            (TextPosition start, TextPosition end) = SelectionRange();
            _selection.Clear();
            ApplyReplace(start, end, text, coalesce: false); // typing over a selection is one step
        }
        else
        {
            ApplyReplace(Caret, Caret, text, coalesce: SingleLine(text));
        }
    }

    public void Enter()
    {
        string newline = _buffer.DefaultEnding.ToChars();
        if (_selection.IsActive(Cursor))
        {
            (TextPosition start, TextPosition end) = SelectionRange();
            _selection.Clear();
            ApplyReplace(start, end, newline, coalesce: false);
        }
        else
        {
            ApplyReplace(Caret, Caret, newline, coalesce: false); // a line break ends any typing run
        }
    }

    public void Backspace()
    {
        if (_selection.IsActive(Cursor)) { DeleteSelection(); return; }

        TextPosition head = Caret;
        if (head is { Row: 0, Col: 0 }) return;

        TextPosition start = head.Col > 0
            ? new TextPosition(head.Row, head.Col - 1)
            : new TextPosition(head.Row - 1, _buffer.LineLength(head.Row - 1));
        ApplyReplace(start, head, "", coalesce: true);
    }

    public void Delete()
    {
        if (_selection.IsActive(Cursor)) { DeleteSelection(); return; }

        TextPosition head = Caret;
        TextPosition end;
        if (head.Col < _buffer.LineLength(head.Row)) end = new TextPosition(head.Row, head.Col + 1);
        else if (head.Row < _buffer.LineCount - 1) end = new TextPosition(head.Row + 1, 0);
        else return; // end of the last line: nothing ahead
        ApplyReplace(head, end, "", coalesce: true);
    }

    // ---- selection-aware movement ------------------------------------------

    /// <summary>
    /// Run a cursor move, managing the selection around it: extend from the anchor
    /// when <paramref name="extend"/> (Shift held), otherwise collapse the selection.
    /// </summary>
    public void Move(Action move, bool extend)
    {
        if (extend) { if (!_selection.HasAnchor) _selection.AnchorAt(Cursor); }
        else _selection.Clear();
        move();
    }

    public void PageUp(int rows) { for (int i = 0; i < rows; i++) Cursor.Up(); }
    public void PageDown(int rows) { for (int i = 0; i < rows; i++) Cursor.Down(); }

    /// <summary>Collapse any selection without moving the caret (Esc).</summary>
    public void ClearSelection() => _selection.Clear();

    /// <summary>
    /// Jump to a 1-based line, as the go-to-line prompt and <c>+LINE</c> both do.
    /// Returns false — and does not move — when the line is outside the buffer, so
    /// the caller can say so rather than silently landing somewhere else.
    /// </summary>
    public bool TryGoToLine(int oneBasedLine)
    {
        if (oneBasedLine < 1 || oneBasedLine > _buffer.LineCount) return false;
        Move(() => Cursor.MoveTo(oneBasedLine - 1, 0), extend: false);
        return true;
    }

    public void SelectAll()
    {
        Cursor.DocumentStart();
        _selection.AnchorAt(Cursor);
        Cursor.DocumentEnd();
    }

    /// <summary>
    /// Select [start, end) and leave the caret on its far end — the same gesture
    /// <see cref="SelectAll"/> makes, aimed at an arbitrary span. This is all a
    /// search hit needs to become visible: the view paints whatever
    /// <see cref="Selection"/> holds, and the next frame scrolls to the caret.
    /// </summary>
    public void SelectRange(TextPosition start, TextPosition end)
    {
        _selection.Clear();
        Cursor.MoveTo(start.Row, start.Col);
        _selection.AnchorAt(Cursor);
        Cursor.MoveTo(end.Row, end.Col);
    }

    /// <summary>
    /// Lines, words and characters for the selection — what ^W reports when there is
    /// one. Zeroes when there is not, so the caller checks
    /// <see cref="HasSelection"/> rather than reading meaning into an empty count.
    ///
    /// It lives here rather than in the loop because the selection's coordinates go
    /// through the clamping <see cref="SelectionRange"/>, and nothing outside this
    /// class gets to index the buffer with a raw anchor.
    /// </summary>
    public TextStats CountSelection()
    {
        if (!HasSelection) return default;
        (TextPosition start, TextPosition end) = SelectionRange();
        return TextStats.Of(_buffer.GetRange(start, end));
    }

    /// <summary>
    /// The live selection as a replace bound, or null when there is none. Like
    /// <see cref="CountSelection"/> this exists so the coordinates leave the class
    /// already clamped — nothing outside it gets to index the buffer with a raw anchor.
    ///
    /// ^R has to call this <b>before</b> its first search: a hit calls
    /// <see cref="SelectRange"/> and overwrites the very selection being asked about.
    /// </summary>
    public ReplaceScope? CurrentSelection()
    {
        if (!HasSelection) return null;
        (TextPosition start, TextPosition end) = SelectionRange();
        return new ReplaceScope(start, end);
    }

    // ---- search / replace ---------------------------------------------------

    /// <summary>The remembered term and toggles, for the prompt to pre-fill and F3 to repeat.</summary>
    public SearchState Search { get; } = new();

    /// <summary>The span the last successful search landed on, for replace to act upon.</summary>
    public SearchMatch? LastMatch { get; private set; }

    /// <summary>Search for a new term, remembering it for <see cref="FindAgain"/>.</summary>
    public SearchOutcome Find(SearchQuery query, bool backwards)
    {
        if (query.Text.Length == 0) return SearchOutcome.NoQuery;
        Search.Text = query.Text;
        Search.MatchCase = query.MatchCase;
        Search.WholeWord = query.WholeWord;
        return Seek(query, backwards);
    }

    /// <summary>Repeat the last search (F3 / Shift+F3), with no prompt.</summary>
    public SearchOutcome FindAgain(bool backwards)
        => Search.HasQuery ? Seek(Search.Query, backwards) : SearchOutcome.NoQuery;

    // Forward searches run from the caret, which after a hit sits on the far end of
    // that hit — so repeating naturally steps to the next one. Backwards is the case
    // that needs help: the caret is past the current match, so searching from it
    // would find the very same match again and Shift+F3 would never move. Start from
    // the near end of the selection instead.
    private SearchOutcome Seek(SearchQuery query, bool backwards)
    {
        TextPosition from = Caret;
        if (backwards && _selection.IsActive(Cursor)) from = SelectionRange().Start;

        bool wrapped;
        SearchMatch? hit = backwards
            ? TextSearch.FindPrevious(_buffer, from, query, out wrapped)
            : TextSearch.FindNext(_buffer, from, query, out wrapped);

        // Nothing found leaves the caret exactly where it was — the whole point of
        // reporting rather than jumping somewhere plausible.
        if (hit is not { } match) return SearchOutcome.NotFound;

        LastMatch = match;
        SelectRange(match.Start, match.End);
        return wrapped ? SearchOutcome.FoundWrapped : SearchOutcome.Found;
    }

    /// <summary>
    /// Replace one match, as its own undo step. The caret lands past the inserted
    /// text, which is where the next search must resume — resuming from the match
    /// *start* would find the replacement inside itself and turn "a" → "aa" into a
    /// loop that never ends.
    /// </summary>
    public void ReplaceMatch(SearchMatch match, string replacement)
    {
        LastMatch = null;
        ApplyReplace(match.Start, match.End, replacement, coalesce: false);
    }

    /// <summary>
    /// Replace every match at or after <paramref name="from"/>, and ending at or
    /// before <paramref name="to"/>, as a <b>single</b> undo step, and return how
    /// many. Returns 0 and touches nothing when the term does not occur.
    ///
    /// <paramref name="to"/> is how answering A to a replace-in-selection stays inside
    /// the selection.
    ///
    /// The single step needs no compound-edit machinery, because <see cref="Edit"/>
    /// is already "at Start, Removed became Inserted": the whole run is one edit
    /// spanning the first hit to the last, with the replacements baked into the
    /// inserted text. The cost is that the undo entry holds that span twice over —
    /// on a file where the first and last hits bracket everything, that is the file
    /// twice. At this editor's few-MB target that is cheaper than a second kind of
    /// undo record, and undo is not where this project wants more moving parts.
    /// </summary>
    public int ReplaceAll(SearchQuery query, string replacement,
                         TextPosition? from = null, TextPosition? to = null)
    {
        List<SearchMatch> matches = TextSearch.FindAll(_buffer, query, from, to);
        if (matches.Count == 0) return 0;

        TextPosition spanStart = matches[0].Start;
        TextPosition spanEnd = matches[^1].End;

        // Stitch the new span out of the gaps between matches. GetRange is what
        // reconstructs the real per-line terminators, so a multi-line span survives
        // this byte-for-byte and mixed endings stay mixed.
        var rewritten = new StringBuilder();
        TextPosition at = spanStart;
        foreach (SearchMatch match in matches)
        {
            rewritten.Append(_buffer.GetRange(at, match.Start));
            rewritten.Append(replacement);
            at = match.End;
        }

        LastMatch = null;
        ApplyReplace(spanStart, spanEnd, rewritten.ToString(), coalesce: false);
        return matches.Count;
    }

    // ---- clipboard ----------------------------------------------------------

    // Copy collapses the selection, unlike Notepad and VS Code, which leave it
    // painted. Deliberate: the highlight left behind after a copy reads as "still
    // armed" and the only way to drop it was to move the caret. The cost is that
    // Ctrl+C twice no longer re-copies the same span.
    public void Copy()
    {
        if (!_selection.IsActive(Cursor)) return;
        (TextPosition start, TextPosition end) = SelectionRange();
        _clipboard.SetText(ToCrlf(_buffer.GetRange(start, end)));
        _selection.Clear();
    }

    public void Cut()
    {
        if (!_selection.IsActive(Cursor)) return;
        (TextPosition start, TextPosition end) = SelectionRange();
        _clipboard.SetText(ToCrlf(_buffer.GetRange(start, end)));
        _selection.Clear();
        ApplyReplace(start, end, "", coalesce: false);
    }

    public void Paste()
    {
        string raw = _clipboard.GetText();
        if (string.IsNullOrEmpty(raw)) return;
        string text = NormalizeNewlines(raw, _buffer.DefaultEnding);

        if (_selection.IsActive(Cursor))
        {
            (TextPosition start, TextPosition end) = SelectionRange();
            _selection.Clear();
            ApplyReplace(start, end, text, coalesce: false);
        }
        else
        {
            ApplyReplace(Caret, Caret, text, coalesce: false);
        }
    }

    public void DeleteSelection()
    {
        if (!_selection.IsActive(Cursor)) return;
        (TextPosition start, TextPosition end) = SelectionRange();
        _selection.Clear();
        ApplyReplace(start, end, "", coalesce: false);
    }

    // ---- line cut / paste (nano ^K / ^U) -----------------------------------

    public void CutLine()
    {
        int row = Cursor.Row;
        var start = new TextPosition(row, 0);
        // Take the terminator too, unless this is an unterminated last line.
        TextPosition end = _buffer.LineAt(row).Ending != LineEnding.None && row < _buffer.LineCount - 1
            ? new TextPosition(row + 1, 0)
            : new TextPosition(row, _buffer.LineLength(row));

        _lineRegister = _buffer.GetRange(start, end);
        _selection.Clear();
        Cursor.MoveTo(row, 0);
        ApplyReplace(start, end, "", coalesce: false);
    }

    public void PasteLine()
    {
        if (_lineRegister.Length == 0) return;
        _selection.Clear();
        ApplyReplace(Caret, Caret, _lineRegister, coalesce: false);
    }

    // ---- undo / redo --------------------------------------------------------

    public bool Undo() { _selection.Clear(); return _undo.Undo(_buffer, Cursor); }
    public bool Redo() { _selection.Clear(); return _undo.Redo(_buffer, Cursor); }

    // ---- the one primitive every edit reduces to ---------------------------

    // Replace [start, end) with `inserted`, move the caret past it, and record the
    // inverse for undo. `coalesce` lets a run of typing or deletes merge into one
    // undo step; the stack still refuses to merge anything that spans a line.
    private void ApplyReplace(TextPosition start, TextPosition end, string inserted, bool coalesce)
    {
        // No selection survives an edit, so drop the anchor here rather than relying
        // on each caller. The ones that consume a selection already clear it; the
        // ones that don't used to leave a *collapsed* anchor behind — Shift+Right
        // then Shift+Left sets an anchor that IsActive reports as inactive, so
        // Backspace and Delete took the no-selection path and left it in place.
        // Join two lines from there and the anchor names a row the buffer no longer
        // has; re-extend with Shift and the next Copy indexes off the end and takes
        // the editor down with the user's unsaved buffer.
        _selection.Clear();

        TextPosition caretBefore = Caret;
        string removed = _buffer.GetRange(start, end);
        if (end != start) _buffer.DeleteRange(start, end);
        TextPosition after = inserted.Length > 0 ? _buffer.InsertMultiline(start, inserted) : start;
        Cursor.MoveTo(after.Row, after.Col);
        _undo.Record(new Edit(start, removed, inserted, caretBefore, Caret, _undo.Now, coalesce));
    }

    private static bool SingleLine(string s) => s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0;

    // Windows apps (Notepad, browsers) expect CRLF on the clipboard.
    private static string ToCrlf(string s)
        => s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    // Incoming clipboard text adopts the buffer's dominant ending so a paste doesn't
    // smuggle foreign line endings into the file.
    private static string NormalizeNewlines(string s, LineEnding ending)
    {
        string lf = s.Replace("\r\n", "\n").Replace('\r', '\n');
        return ending switch
        {
            LineEnding.CrLf => lf.Replace("\n", "\r\n"),
            LineEnding.Cr => lf.Replace('\n', '\r'),
            _ => lf,
        };
    }
}
