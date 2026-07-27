namespace Nib.Model;

/// <summary>
/// A (row, char-column) point in the buffer, where the column is a UTF-16 index
/// into the line's text — the same coordinate space as <see cref="Cursor"/> and the
/// <see cref="TextBuffer"/> edit ops. Ordered so a selection can be normalized into
/// a start/end pair regardless of which way the user dragged.
/// </summary>
public readonly record struct TextPosition(int Row, int Col) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other)
    {
        int byRow = Row.CompareTo(other.Row);
        return byRow != 0 ? byRow : Col.CompareTo(other.Col);
    }

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;

    public static TextPosition Min(TextPosition a, TextPosition b) => a <= b ? a : b;
    public static TextPosition Max(TextPosition a, TextPosition b) => a >= b ? a : b;
}
