using Nib.Model;

namespace Nib.Commands;

/// <summary>
/// The verbs the keymap invokes: each pairs a buffer mutation with the matching
/// cursor move, so the two never drift apart. This is the only place that knows,
/// for example, that a backspace at column 0 lands the caret at the end of the
/// line it just joined onto.
/// </summary>
public sealed class EditorCommands
{
    private readonly TextBuffer _buffer;

    public Cursor Cursor { get; }

    public EditorCommands(TextBuffer buffer, Cursor cursor)
    {
        _buffer = buffer;
        Cursor = cursor;
    }

    public void InsertChar(char ch)
    {
        _buffer.InsertChar(Cursor.Row, Cursor.Col, ch);
        Cursor.MoveTo(Cursor.Row, Cursor.Col + 1);
    }

    /// <summary>Insert text with no newline — a typed char or an astral surrogate pair (2 UTF-16 units).</summary>
    public void Insert(string text)
    {
        _buffer.InsertText(Cursor.Row, Cursor.Col, text);
        Cursor.MoveTo(Cursor.Row, Cursor.Col + text.Length);
    }

    public void Enter()
    {
        _buffer.SplitLine(Cursor.Row, Cursor.Col);
        Cursor.MoveTo(Cursor.Row + 1, 0);
    }

    public void Backspace()
    {
        int row = Cursor.Row, col = Cursor.Col;
        if (col > 0)
        {
            _buffer.DeleteBackward(row, col);
            Cursor.MoveTo(row, col - 1);
        }
        else if (row > 0)
        {
            int prevLen = _buffer.LineLength(row - 1); // caret lands where the join happened
            _buffer.DeleteBackward(row, col);
            Cursor.MoveTo(row - 1, prevLen);
        }
    }

    public void Delete()
    {
        _buffer.DeleteForward(Cursor.Row, Cursor.Col);
        Cursor.MoveTo(Cursor.Row, Cursor.Col); // re-clamp; caret stays put
    }

    public void PageUp(int rows)
    {
        for (int i = 0; i < rows; i++) Cursor.Up();
    }

    public void PageDown(int rows)
    {
        for (int i = 0; i < rows; i++) Cursor.Down();
    }
}
