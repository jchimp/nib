using Nib.Highlight;
using Nib.Model;
using Nib.Terminal;

namespace Nib.Ui;

/// <summary>
/// What the message row is saying, which decides how it is painted. The row
/// carries all three and used to render them identically — a failed save looked
/// exactly like a successful one.
/// </summary>
public enum MessageKind
{
    /// <summary>Something happened and went fine. "Wrote 42 lines".</summary>
    Info,

    /// <summary>Something failed. "Error: access denied".</summary>
    Error,

    /// <summary>Waiting on the user. A save-as prompt, or a Y/N/Esc question.</summary>
    Prompt,
}

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

    // Message row. Three tiers, because the row carries all three and painting them
    // alike meant a failed save read exactly like a successful one. Kept clear of the
    // bars' teal and of DefaultSelectionBg, so none of them can be mistaken for
    // another when they share a screen.
    private static readonly Color MessageInfoFg = Color.Rgb(0xDC, 0xE7, 0xF0);
    private static readonly Color MessageInfoBg = Color.Rgb(0x2C, 0x3E, 0x50);
    private static readonly Color MessageErrorFg = Color.Rgb(0xFF, 0xE5, 0xE5);
    private static readonly Color MessageErrorBg = Color.Rgb(0x8B, 0x22, 0x22);
    private static readonly Color MessagePromptFg = Color.Rgb(0x1A, 0x1A, 0x1A);
    private static readonly Color MessagePromptBg = Color.Rgb(0xD7, 0xA2, 0x1A);

    // One space of background either side of the text, so the run reads as a label
    // rather than as body text that happens to be coloured. The caret has to clear
    // the same pad, or it lands one cell left of the character it is on.
    private const int MessagePad = 1;

    // Between the typed term and the search status. Wider than MessagePad so the two
    // colour runs read as separate things rather than as one wrapped sentence.
    private const int StatusGap = 2;

    private readonly Screen _screen;
    private readonly Viewport _viewport;
    private readonly TextBuffer _buffer;
    private readonly Cursor _cursor;

    private string _message = "";

    /// <summary>
    /// Transient feedback shown on the message row when no prompt is active
    /// ("Wrote 42 lines"). Assigning resets the kind to <see cref="MessageKind.Info"/>
    /// — deliberately, so a previous error's styling can never outlive its text and
    /// paint the next success in red.
    /// </summary>
    public string Message
    {
        get => _message;
        set { _message = value; MessageKind = MessageKind.Info; }
    }

    /// <summary>How <see cref="Message"/> is painted. Set it through <see cref="SetMessage"/>.</summary>
    public MessageKind MessageKind { get; private set; }

    /// <summary>Set the message text and its severity together.</summary>
    public void SetMessage(string text, MessageKind kind)
    {
        _message = text;
        MessageKind = kind;
    }

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

    /// <summary>
    /// Paint a full-screen page — the ^H keymap — over the buffer: a title bar, the
    /// lines, and a footer bar saying how to leave. Anything past the bottom of the
    /// window is clipped rather than scrolled, which is why
    /// <see cref="HelpScreen.Lines"/> is sized to fit a 24-row terminal.
    ///
    /// The caret is parked on the footer. There is nothing to type into, and a caret
    /// blinking in the middle of a page of text reads as an edit position.
    /// </summary>
    public void RenderOverlay(string title, IReadOnlyList<string> lines, string footer)
    {
        _screen.Clear(Cell.Blank);

        DrawBar(0, title);

        int last = _screen.Height - 1;
        for (int i = 0; i < lines.Count; i++)
        {
            int y = i + 1;
            if (y >= last) break;
            string text = lines[i];
            if (text.Length > _screen.Width) text = text[.._screen.Width];
            _screen.PutText(0, y, text, Color.Default, Color.Default);
        }

        DrawBar(last, footer);
        CursorX = 0;
        CursorY = Math.Max(0, last);
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
                    // gutter + sx, exactly as the glyph branch below. A tab paints
                    // several cells rather than one, so the offset is easy to drop
                    // here and the mistake is invisible in the text: the glyphs after
                    // the tab still land correctly and only the tab's own cells are
                    // wrong, over the top of the line number.
                    int sx = col - first;
                    if (sx >= 0 && sx < width) _screen.Set(gutter + sx, y, ' ', Color.Default, bg);
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

    // The message row is the only chrome that is blank most of the time, so it gets
    // no background of its own until it has something to say — the colour appearing
    // is itself the signal. The run hugs the text with one space either side rather
    // than filling the row: a full-width band directly above the two help bars reads
    // as a third bar and crowds the bottom of the screen.
    private void DrawMessage()
    {
        int y = MessageRow;
        if (y <= 0) return;

        if (ActivePrompt is { } prompt) { DrawPrompt(y, prompt); return; }
        if (Message.Length == 0) return;

        (Color fg, Color bg) = MessageColors(MessageKind);

        string padded = new string(' ', MessagePad) + Message + new string(' ', MessagePad);
        if (padded.Length > _screen.Width) padded = padded[.._screen.Width];
        _screen.PutText(0, y, padded, fg, bg);
    }

    // A prompt is up to three runs rather than one string, because the status has to
    // carry its own colour: "Not found" must read as an error while sharing a row with
    // the amber prompt that is still waiting on a keystroke. The gap and the closing
    // pad stay in the prompt's colour so the run opens and closes the same way.
    //
    // The caret is placed off Label.Length + Caret (see PlaceCursor), so a run appended
    // after the input cannot disturb it.
    private void DrawPrompt(int y, Prompt prompt)
    {
        (Color fg, Color bg) = MessageColors(MessageKind.Prompt);

        string head = new string(' ', MessagePad) + prompt.Label + prompt.Input;
        if (head.Length >= _screen.Width)
        {
            _screen.PutText(0, y, head.AsSpan(0, _screen.Width), fg, bg);
            return;
        }

        _screen.PutText(0, y, head, fg, bg);
        int x = head.Length;

        if (prompt.Status.Length > 0)
        {
            int gap = Math.Min(StatusGap, _screen.Width - x);
            _screen.PutText(x, y, new string(' ', gap), fg, bg);
            x += gap;

            // Leave room for the closing pad: a status clipped flush to the last cell
            // would make the run look truncated even when it is not.
            int room = _screen.Width - x - MessagePad;
            if (room <= 0) return;

            int shown = Math.Min(prompt.Status.Length, room);
            (Color statusFg, Color statusBg) = MessageColors(prompt.StatusKind);
            _screen.PutText(x, y, prompt.Status.AsSpan(0, shown), statusFg, statusBg);
            x += shown;
        }

        int pad = Math.Min(MessagePad, _screen.Width - x);
        if (pad > 0) _screen.PutText(x, y, new string(' ', pad), fg, bg);
    }

    private static (Color Fg, Color Bg) MessageColors(MessageKind kind) => kind switch
    {
        MessageKind.Error => (MessageErrorFg, MessageErrorBg),
        // Dark text on amber, inverted against the other two on purpose: a row that
        // is waiting on a keystroke should not look like a row that is merely
        // reporting one.
        MessageKind.Prompt => (MessagePromptFg, MessagePromptBg),
        _ => (MessageInfoFg, MessageInfoBg),
    };

    private void DrawHelp()
    {
        DrawHelpRow(HelpRow1, HelpBar.Row1);
        DrawHelpRow(HelpRow2, HelpBar.Row2);
    }

    private void DrawHelpRow(int y, string text)
    {
        if (y <= 0) return;
        DrawBar(y, text);
    }

    private void DrawBar(int y, string text)
    {
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
            // + MessagePad: DrawMessage indents the prompt by its leading pad space.
            CursorX = Math.Min(_screen.Width - 1, MessagePad + p.Label.Length + p.Caret);
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
