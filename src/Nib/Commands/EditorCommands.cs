using Nib.Model;
using Nib.Model.Undo;

namespace Nib.Commands;

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

    public void SelectAll()
    {
        Cursor.DocumentStart();
        _selection.AnchorAt(Cursor);
        Cursor.DocumentEnd();
    }

    // ---- clipboard ----------------------------------------------------------

    public void Copy()
    {
        if (!_selection.IsActive(Cursor)) return;
        (TextPosition start, TextPosition end) = SelectionRange();
        _clipboard.SetText(ToCrlf(_buffer.GetRange(start, end)));
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
