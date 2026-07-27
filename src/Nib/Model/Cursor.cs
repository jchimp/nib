namespace Nib.Model;

/// <summary>
/// The insertion point: a (row, char-column) pair over a <see cref="TextBuffer"/>,
/// plus the "desired" display column that vertical movement tries to preserve.
///
/// Desired-column memory is the behaviour you feel when you hold Down through a
/// short line: the caret snaps left on the short line but returns to its original
/// column on the next long one. It is tracked as a *display* column (tabs
/// expanded) so movement stays visually straight, which is why <see cref="Cursor"/>
/// needs <see cref="TabStops"/> — and why that math had to leave <c>Ui/</c>.
///
/// Horizontal moves and edits reset the desired column; vertical moves keep it.
/// </summary>
public sealed class Cursor
{
    private readonly TextBuffer _buffer;
    private readonly int _tabWidth;

    public int Row { get; private set; }
    public int Col { get; private set; }
    public int DesiredColumn { get; private set; }

    public Cursor(TextBuffer buffer, int tabWidth = TabStops.DefaultTabWidth)
    {
        _buffer = buffer;
        _tabWidth = tabWidth;
    }

    private string CurrentLine => _buffer.GetLine(Row);

    /// <summary>Display column of the caret, for placing the hardware cursor and horizontal scroll.</summary>
    public int DisplayColumn => TabStops.CharToDisplayColumn(CurrentLine, Col, _tabWidth);

    /// <summary>
    /// Move to an explicit position, clamping into the buffer, and refresh the
    /// desired column from the result. Edits and horizontal moves use this.
    /// </summary>
    public void MoveTo(int row, int col)
    {
        Row = Math.Clamp(row, 0, _buffer.LineCount - 1);
        Col = Math.Clamp(col, 0, CurrentLine.Length);
        RememberColumn();
    }

    /// <summary>Recompute the desired column from the current caret (call after an edit that kept the caret put).</summary>
    public void RememberColumn() => DesiredColumn = DisplayColumn;

    public void Left()
    {
        if (Col > 0) Col--;
        else if (Row > 0) { Row--; Col = CurrentLine.Length; }
        RememberColumn();
    }

    public void Right()
    {
        if (Col < CurrentLine.Length) Col++;
        else if (Row < _buffer.LineCount - 1) { Row++; Col = 0; }
        RememberColumn();
    }

    public void Up()
    {
        if (Row == 0) { Col = 0; RememberColumn(); return; }
        Row--;
        Col = TabStops.DisplayToCharColumn(CurrentLine, DesiredColumn, _tabWidth);
    }

    public void Down()
    {
        if (Row >= _buffer.LineCount - 1) { Col = CurrentLine.Length; RememberColumn(); return; }
        Row++;
        Col = TabStops.DisplayToCharColumn(CurrentLine, DesiredColumn, _tabWidth);
    }

    public void Home() => MoveTo(Row, 0);

    public void End() => MoveTo(Row, CurrentLine.Length);

    public void DocumentStart() => MoveTo(0, 0);

    public void DocumentEnd() => MoveTo(_buffer.LineCount - 1, _buffer.GetLine(_buffer.LineCount - 1).Length);

    /// <summary>Move left to the start of the previous word (whitespace-delimited).</summary>
    public void WordLeft()
    {
        if (Col == 0) { Left(); return; }
        string text = CurrentLine;
        int i = Col;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        MoveTo(Row, i);
    }

    /// <summary>Move right to the start of the next word.</summary>
    public void WordRight()
    {
        string text = CurrentLine;
        if (Col >= text.Length) { Right(); return; }
        int i = Col;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        MoveTo(Row, i);
    }
}
