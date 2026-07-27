namespace Nib.Model;

/// <summary>
/// The selected span, as an <see cref="Anchor"/> fixed where the selection began
/// plus the live <see cref="Cursor"/> as its moving head. Storing only the anchor
/// (not both ends) means the head follows the cursor for free: Shift+movement moves
/// the cursor and the selection stretches to match.
///
/// Console-free by rule; the view reads <see cref="ContainsRow"/> to paint it.
/// </summary>
public sealed class Selection
{
    /// <summary>Where the selection was started, or null when there is no selection.</summary>
    public TextPosition? Anchor { get; private set; }

    public bool HasAnchor => Anchor.HasValue;

    /// <summary>Drop the anchor at the cursor — the start of a Shift+movement gesture.</summary>
    public void AnchorAt(Cursor cursor) => Anchor = new TextPosition(cursor.Row, cursor.Col);

    public void Clear() => Anchor = null;

    /// <summary>The normalized [start, end] span, ordered regardless of drag direction.</summary>
    public (TextPosition Start, TextPosition End) Range(Cursor cursor)
    {
        var head = new TextPosition(cursor.Row, cursor.Col);
        TextPosition anchor = Anchor ?? head;
        return (TextPosition.Min(anchor, head), TextPosition.Max(anchor, head));
    }

    /// <summary>True only when something is actually selected (anchored and non-empty).</summary>
    public bool IsActive(Cursor cursor)
    {
        if (!HasAnchor) return false;
        (TextPosition start, TextPosition end) = Range(cursor);
        return start != end;
    }

    /// <summary>
    /// The selected column span on <paramref name="row"/>, for the view. Rows between
    /// the endpoints report <see cref="int.MaxValue"/> as <paramref name="endCol"/>
    /// (select to end of line); the caller clamps to the real line length.
    /// </summary>
    public bool ContainsRow(int row, Cursor cursor, out int startCol, out int endCol)
    {
        startCol = 0;
        endCol = 0;
        if (!HasAnchor) return false;

        (TextPosition start, TextPosition end) = Range(cursor);
        if (start == end || row < start.Row || row > end.Row) return false;

        startCol = row == start.Row ? start.Col : 0;
        endCol = row == end.Row ? end.Col : int.MaxValue;
        return true;
    }
}
