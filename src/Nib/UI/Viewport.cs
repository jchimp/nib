using Nib.Model;

namespace Nib.Ui;

/// <summary>
/// The scrolled window onto a document: which line sits at the top of the screen
/// and which display column sits at the left edge. The tab-stop arithmetic now
/// lives in <see cref="TabStops"/> (console-free, so the phase-3 cursor can share
/// it); this class keeps only the scroll state and delegates the math, so its
/// public shape — and <c>ViewportTests</c> — are unchanged.
///
/// Deliberately console-free: no reference to <c>Terminal/</c>.
/// </summary>
public sealed class Viewport
{
    /// <summary>nano's default tab size. Config override arrives in phase 6.</summary>
    public const int DefaultTabWidth = TabStops.DefaultTabWidth;

    /// <summary>0-based index of the document line drawn on the top text row.</summary>
    public int FirstLine { get; private set; }

    /// <summary>0-based display column drawn at the left screen edge.</summary>
    public int FirstColumn { get; private set; }

    public int TabWidth { get; }

    public Viewport(int tabWidth = DefaultTabWidth) => TabWidth = tabWidth;

    // ---- scrolling ----------------------------------------------------------
    // Callers adjust, then clamp with the totals they know. Splitting the two
    // keeps this class ignorant of document length until it's actually handed in.

    public void ScrollLines(int delta) => FirstLine = Math.Max(0, FirstLine + delta);

    public void ScrollColumns(int delta) => FirstColumn = Math.Max(0, FirstColumn + delta);

    /// <summary>Keep the top line in range. The last line may sit at the top; rows below it paint blank.</summary>
    public void ClampVertical(int totalLines)
    {
        int max = Math.Max(0, totalLines - 1);
        if (FirstLine > max) FirstLine = max;
        if (FirstLine < 0) FirstLine = 0;
    }

    /// <summary>Keep the left column in range against the widest visible line's display width.</summary>
    public void ClampHorizontal(int maxDisplayWidth)
    {
        int max = Math.Max(0, maxDisplayWidth - 1);
        if (FirstColumn > max) FirstColumn = max;
        if (FirstColumn < 0) FirstColumn = 0;
    }

    /// <summary>
    /// Scroll the minimum amount so the cell at (<paramref name="cursorLine"/>,
    /// <paramref name="cursorDisplayColumn"/>) is inside a <paramref name="textRows"/> ×
    /// <paramref name="textColumns"/> window. Pure arithmetic — the phase-3 edit loop
    /// calls it after every cursor move.
    /// </summary>
    public void EnsureVisible(int cursorLine, int cursorDisplayColumn, int textRows, int textColumns)
    {
        if (textRows > 0)
        {
            if (cursorLine < FirstLine) FirstLine = cursorLine;
            else if (cursorLine >= FirstLine + textRows) FirstLine = cursorLine - textRows + 1;
        }
        if (textColumns > 0)
        {
            if (cursorDisplayColumn < FirstColumn) FirstColumn = cursorDisplayColumn;
            else if (cursorDisplayColumn >= FirstColumn + textColumns) FirstColumn = cursorDisplayColumn - textColumns + 1;
        }
        if (FirstLine < 0) FirstLine = 0;
        if (FirstColumn < 0) FirstColumn = 0;
    }

    // ---- tab-stop math (delegated to TabStops) ------------------------------

    /// <summary>The display column a tab lands on when it starts at <paramref name="col"/>.</summary>
    public static int NextTabStop(int col, int tabWidth) => TabStops.NextTabStop(col, tabWidth);

    /// <summary>Total display columns the line occupies once tabs are expanded.</summary>
    public int DisplayWidth(ReadOnlySpan<char> line) => TabStops.DisplayWidth(line, TabWidth);

    /// <summary>Display column at which the character at <paramref name="charIndex"/> begins.</summary>
    public int CharToDisplayColumn(ReadOnlySpan<char> line, int charIndex) =>
        TabStops.CharToDisplayColumn(line, charIndex, TabWidth);

    /// <summary>Inverse of <see cref="CharToDisplayColumn"/>.</summary>
    public int DisplayToCharColumn(ReadOnlySpan<char> line, int displayColumn) =>
        TabStops.DisplayToCharColumn(line, displayColumn, TabWidth);

    /// <summary>Tabs expanded to spaces. Convenience for tests; the renderer expands inline.</summary>
    public string ExpandLine(string line) => TabStops.ExpandLine(line, TabWidth);
}
