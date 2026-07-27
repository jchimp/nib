namespace Nib.Model;

/// <summary>
/// The character-index ↔ display-column arithmetic, tabs expanded to the next
/// stop. Pure and console-free so <see cref="Cursor"/> can use it for
/// desired-column memory without <c>Model/</c> reaching into <c>Ui/</c>. The
/// read-only <c>Ui/Viewport</c> delegates its own math here so there is one
/// implementation, not two.
/// </summary>
public static class TabStops
{
    /// <summary>nano's default tab size. The config override arrives in phase 6.</summary>
    public const int DefaultTabWidth = 8;

    /// <summary>The display column a tab lands on when it starts at <paramref name="col"/>.</summary>
    public static int NextTabStop(int col, int tabWidth) => (col / tabWidth + 1) * tabWidth;

    /// <summary>Total display columns the line occupies once tabs are expanded.</summary>
    public static int DisplayWidth(ReadOnlySpan<char> line, int tabWidth)
    {
        int col = 0;
        foreach (char c in line)
            col = c == '\t' ? NextTabStop(col, tabWidth) : col + 1;
        return col;
    }

    /// <summary>Display column at which the character at <paramref name="charIndex"/> begins.</summary>
    public static int CharToDisplayColumn(ReadOnlySpan<char> line, int charIndex, int tabWidth)
    {
        int col = 0;
        int end = Math.Min(charIndex, line.Length);
        for (int i = 0; i < end; i++)
            col = line[i] == '\t' ? NextTabStop(col, tabWidth) : col + 1;
        return col;
    }

    /// <summary>
    /// Inverse of <see cref="CharToDisplayColumn"/>: the char index whose glyph
    /// covers <paramref name="displayColumn"/>. A column landing inside an
    /// expanded tab resolves to that tab's index.
    /// </summary>
    public static int DisplayToCharColumn(ReadOnlySpan<char> line, int displayColumn, int tabWidth)
    {
        int col = 0;
        for (int i = 0; i < line.Length; i++)
        {
            int next = line[i] == '\t' ? NextTabStop(col, tabWidth) : col + 1;
            if (next > displayColumn) return i;
            col = next;
        }
        return line.Length;
    }

    /// <summary>Tabs expanded to spaces. Convenience for tests; the renderer expands inline.</summary>
    public static string ExpandLine(ReadOnlySpan<char> line, int tabWidth)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        int col = 0;
        foreach (char c in line)
        {
            if (c == '\t')
            {
                int stop = NextTabStop(col, tabWidth);
                sb.Append(' ', stop - col);
                col = stop;
            }
            else
            {
                sb.Append(c);
                col++;
            }
        }
        return sb.ToString();
    }
}
