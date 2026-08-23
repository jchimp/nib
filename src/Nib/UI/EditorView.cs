using Nib.Highlight;
using Nib.Model;
using Nib.Terminal;

namespace Nib.Ui;

/// <summary>
/// Paints the editable buffer through a <see cref="Viewport"/> onto a
/// <see cref="Screen"/> in the nano layout: a title/status line on top, the text
/// body, then a message/prompt line and a two-row shortcut bar at the bottom.
///
/// It owns the one tricky mapping — character index to display column — so tabs
/// land on stops, horizontal scroll counts columns, and the hardware cursor lands
/// on the glyph the caret is actually on. Where the caret sits is exposed via
/// <see cref="CursorX"/>/<see cref="CursorY"/> for the loop to position it after
/// the frame is flushed.
/// </summary>
public sealed class EditorView
{
    private static readonly Color BarFg = Color.Rgb(0xFF, 0xFF, 0xFF);
    private static readonly Color BarBg = Color.Rgb(0x00, 0x80, 0x80);
    // Used when the active theme declares no editor.selectionBackground, and by the
    // no-highlighting path. Foreground is left alone so text stays readable on it.
    private static readonly Color DefaultSelectionBg = Color.Rgb(0x26, 0x4F, 0x78);
    // Deliberately dim and theme-independent. The gutter is chrome, not content: it
    // has to stay quieter than the least important token on screen or it competes
    // with the text for attention. A theme hook (editorLineNumber.foreground) can
    // come later if a theme ever looks wrong with it.
    private static readonly Color GutterFg = Color.Rgb(0x6E, 0x76, 0x81);

    // Below this many columns of text, the gutter costs more than it is worth and
    // is dropped rather than squeezing the content it exists to index.
    private const int MinTextColumns = 20;

    private readonly Screen _screen;
    private readonly Viewport _viewport;
    private readonly TextBuffer _buffer;
    private readonly Cursor _cursor;

    /// <summary>Transient feedback shown on the message row when no prompt is active ("Wrote 42 lines").</summary>
    public string Message { get; set; } = "";

    /// <summary>When set, the message row becomes this modal input line and the caret moves into it.</summary>
    public Prompt? ActivePrompt { get; set; }

    /// <summary>The live selection to paint, or null when nothing is selected/available.</summary>
    public Selection? Selection { get; set; }

    /// <summary>
    /// Source of token colour. Defaults to the no-colour implementation, so the view
    /// works unchanged when no grammar matched the file.
    /// </summary>
    public IHighlighter Highlighter { get; set; } = NullHighlighter.Instance;

    private Color SelectionBg => Highlighter.SelectionBackground ?? DefaultSelectionBg;

    /// <summary>Screen cell the hardware cursor should sit on, computed by the last <see cref="Render"/>.</summary>
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }

    public EditorView(Screen screen, Viewport viewport, TextBuffer buffer, Cursor cursor)
    {
        _screen = screen;
        _viewport = viewport;
        _buffer = buffer;
        _cursor = cursor;
    }

    // Row assignments. Title on top; the bottom three are the message/prompt line
    // and two help rows. Text fills whatever is left.
    private int MessageRow => _screen.Height - 3;
    private int HelpRow1 => _screen.Height - 2;
    private int HelpRow2 => _screen.Height - 1;

    /// <summary>Text rows between the title and the bottom bars.</summary>
    public int TextRows => Math.Max(0, _screen.Height - 4);

    /// <summary>Whether the line-number gutter is drawn (Alt+N). Off by default, as in nano.</summary>
    public bool ShowLineNumbers { get; set; }

    /// <summary>
    /// Columns the gutter occupies, including its one-space separator; 0 when it is
    /// off or would not leave enough room for text.
    ///
    /// Recomputed per use rather than cached, because the buffer grows: a file that
    /// crosses from 999 to 1000 lines needs a wider gutter on the very next frame,
    /// and a stale width would paint numbers over the first column of text.
    /// </summary>
    public int GutterWidth
    {
        get
        {
            if (!ShowLineNumbers) return 0;
            int width = DigitCount(_buffer.LineCount) + 1;
            return _screen.Width - width >= MinTextColumns ? width : 0;
        }
    }

    /// <summary>
    /// Screen columns available to text, once the gutter has taken its share. The
    /// horizontal scroll must be calibrated against this and not the screen width —
    /// otherwise the caret slides under the gutter on a long line and the rightmost
    /// columns become unreachable.
    /// </summary>
    public int TextColumns => Math.Max(0, _screen.Width - GutterWidth);

    private static int DigitCount(int value)
    {
        int digits = 1;
        while (value >= 10) { value /= 10; digits++; }
        return digits;
    }

    public void Render()
    {
        _screen.Clear(Cell.Blank);

        DrawTitle();

        int rows = TextRows;
        for (int y = 0; y < rows; y++)
        {
            int line = _viewport.FirstLine + y;
            if (line >= _buffer.LineCount) break; // past EOF: leave the row blank
            DrawLine(y + 1, line, _buffer.GetLine(line));
        }

        DrawMessage();
        DrawHelp();
        PlaceCursor();
    }

    private void DrawTitle()
    {
        FillBar(0);
        string title = StatusBar.Build(_buffer, _cursor);
        if (title.Length > _screen.Width) title = title[.._screen.Width];
        _screen.PutText(0, 0, title, BarFg, BarBg);
    }

    // Place each glyph at (displayColumn - FirstColumn), on screen row y. Tabs paint
    // as spaces across their expanded range; other control chars render as a single
    // placeholder so width-1 column math stays exact. Cells whose *character* index
    // falls inside the selection get the selection background.
    //
    // Highlight spans are indexed by the same character index, and arrive ordered
    // and non-overlapping, so one cursor walks them alongside the text rather than
    // searching per character. A row with no cached spans — not tokenized yet, or
    // no grammar — paints in the terminal's own foreground.
    private void DrawLine(int y, int bufferRow, string line)
    {
        int first = _viewport.FirstColumn;
        int gutter = GutterWidth;
        int width = _screen.Width - gutter;
        int col = 0;
        int charIndex = 0;

        DrawGutter(y, bufferRow, gutter);

        int selStart = 0, selEnd = 0;
        bool hasSel = Selection is { } sel && sel.ContainsRow(bufferRow, _cursor, out selStart, out selEnd);

        IReadOnlyList<StyledSpan>? spans = Highlighter.Spans(bufferRow);
        int spanIndex = 0;

        foreach (char ch in line)
        {
            Color bg = hasSel && charIndex >= selStart && charIndex < selEnd ? SelectionBg : Color.Default;

            while (spans is not null && spanIndex < spans.Count &&
                   charIndex >= spans[spanIndex].Start + spans[spanIndex].Length)
            {
                spanIndex++;
            }

            Color fg = Color.Default;
            if (spans is not null && spanIndex < spans.Count && charIndex >= spans[spanIndex].Start)
                fg = spans[spanIndex].Fg;

            if (ch == '\t')
            {
                int stop = Viewport.NextTabStop(col, _viewport.TabWidth);
                for (; col < stop; col++)
                {
                    int sx = col - first;
                    if (sx >= 0 && sx < width) _screen.Set(sx, y, ' ', Color.Default, bg);
                }
            }
            else
            {
                char glyph = ch < ' ' || ch == '\x7f' ? '?' : ch;
                int sx = col - first;
                if (sx >= 0 && sx < width) _screen.Set(gutter + sx, y, glyph, fg, bg);
                col++;
            }

            charIndex++;
            if (col - first >= width) return; // everything else is off the right edge
        }

        // A selection that runs past this line's text includes its line break — show
        // that with one trailing highlighted cell.
        if (hasSel && line.Length < selEnd)
        {
            int sx = col - first;
            if (sx >= 0 && sx < width) _screen.Set(gutter + sx, y, ' ', Color.Default, SelectionBg);
        }
    }

    // Right-aligned in the gutter, with the last column left blank as a separator so
    // the digits never touch the text. Only rows that hold a line get one; past EOF
    // the gutter stays blank, as nano's does.
    private void DrawGutter(int y, int bufferRow, int gutter)
    {
        if (gutter == 0) return;

        for (int x = 0; x < gutter; x++) _screen.Set(x, y, ' ', GutterFg, Color.Default);

        string label = (bufferRow + 1).ToString();
        int x0 = gutter - 1 - label.Length;
        if (x0 >= 0) _screen.PutText(x0, y, label, GutterFg, Color.Default);
    }

    private void DrawMessage()
    {
        int y = MessageRow;
        if (y <= 0) return;
        string text = ActivePrompt is { } p ? p.Label + p.Input : Message;
        if (text.Length > _screen.Width) text = text[.._screen.Width];
        _screen.PutText(0, y, text, Color.Default, Color.Default);
    }

    private void DrawHelp()
    {
        DrawHelpRow(HelpRow1, HelpBar.Row1);
        DrawHelpRow(HelpRow2, HelpBar.Row2);
    }

    private void DrawHelpRow(int y, string text)
    {
        if (y <= 0) return;
        FillBar(y);
        if (text.Length > _screen.Width) text = text[.._screen.Width];
        _screen.PutText(0, y, text, BarFg, BarBg);
    }

    private void FillBar(int y)
    {
        for (int x = 0; x < _screen.Width; x++)
            _screen.Set(x, y, ' ', BarFg, BarBg);
    }

    // The caret sits in the prompt when one is active, otherwise on its glyph in
    // the text body (clamped to the screen so a stale value never points off-grid).
    private void PlaceCursor()
    {
        if (ActivePrompt is { } p)
        {
            CursorX = Math.Min(_screen.Width - 1, p.Label.Length + p.Caret);
            CursorY = Math.Max(0, MessageRow);
            return;
        }

        int gutter = GutterWidth;
        int x = gutter + _cursor.DisplayColumn - _viewport.FirstColumn;
        int y = (_cursor.Row - _viewport.FirstLine) + 1; // +1 for the title row
        CursorX = Math.Clamp(x, gutter, Math.Max(gutter, _screen.Width - 1));
        CursorY = Math.Clamp(y, 1, Math.Max(1, TextRows));
    }
}
