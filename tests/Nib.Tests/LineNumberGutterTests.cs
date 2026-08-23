using Nib.Model;
using Nib.Terminal;
using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The line-number gutter (Alt+N). Almost all of the risk here is arithmetic: the
/// gutter takes columns off the left, and everything that maps a buffer position to
/// a screen column has to agree about how many. A gutter that is drawn correctly but
/// not subtracted from the scroll width looks fine until a line runs off the right
/// edge, so the width is asserted as hard as the painting.
/// </summary>
public class LineNumberGutterTests
{
    private const int Width = 80;
    private const int Height = 24;

    private static (EditorView View, Screen Screen, TextBuffer Buffer, Cursor Cursor, Viewport Viewport)
        Setup(int width = Width, params string[] lines)
    {
        if (lines.Length == 0) lines = ["alpha", "beta", "gamma"];

        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));

        var buffer = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
        var screen = new Screen(width, Height);
        var viewport = new Viewport();
        var cursor = new Cursor(buffer, viewport.TabWidth);
        var view = new EditorView(screen, viewport, buffer, cursor);
        return (view, screen, buffer, cursor, viewport);
    }

    // The first text row is screen row 1: row 0 is the title bar.
    private static string RowText(Screen screen, int y, int from, int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = screen.CellAt(from + i, y).Ch;
        return new string(chars);
    }

    [Fact]
    public void Off_by_default_and_text_starts_at_column_zero()
    {
        (EditorView view, Screen screen, _, _, _) = Setup();
        Assert.False(view.ShowLineNumbers);
        Assert.Equal(0, view.GutterWidth);
        Assert.Equal(Width, view.TextColumns);

        view.Render();
        Assert.Equal("alpha", RowText(screen, 1, 0, 5));
    }

    [Fact]
    public void On_it_numbers_each_row_and_shifts_the_text_right()
    {
        (EditorView view, Screen screen, _, _, _) = Setup();
        view.ShowLineNumbers = true;

        Assert.Equal(2, view.GutterWidth); // 3 lines -> one digit, plus a separator
        view.Render();

        Assert.Equal("1 alpha", RowText(screen, 1, 0, 7));
        Assert.Equal("2 beta", RowText(screen, 2, 0, 6));
        Assert.Equal("3 gamma", RowText(screen, 3, 0, 7));
    }

    [Fact]
    public void Numbers_are_right_aligned_with_a_blank_separator()
    {
        var lines = new string[12];
        for (int i = 0; i < lines.Length; i++) lines[i] = "x";
        (EditorView view, Screen screen, _, _, _) = Setup(Width, lines);
        view.ShowLineNumbers = true;

        Assert.Equal(3, view.GutterWidth); // 12 lines -> two digits, plus a separator
        view.Render();

        Assert.Equal(" 1 x", RowText(screen, 1, 0, 4));   // right-aligned
        Assert.Equal("10 x", RowText(screen, 10, 0, 4));
    }

    [Theory]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(99, 3)]
    [InlineData(100, 4)]
    [InlineData(1000, 5)]
    public void Gutter_widens_with_the_line_count(int lineCount, int expected)
    {
        var lines = new string[lineCount];
        for (int i = 0; i < lineCount; i++) lines[i] = "x";
        (EditorView view, _, _, _, _) = Setup(Width, lines);
        view.ShowLineNumbers = true;

        Assert.Equal(expected, view.GutterWidth);
        Assert.Equal(Width - expected, view.TextColumns);
    }

    [Fact]
    public void A_terminal_too_narrow_to_spare_the_columns_drops_the_gutter()
    {
        (EditorView view, Screen screen, _, _, _) = Setup(width: 16);
        view.ShowLineNumbers = true;

        // Asked for, but not affordable: the text would be left with 14 columns.
        Assert.Equal(0, view.GutterWidth);
        Assert.Equal(16, view.TextColumns);

        view.Render();
        Assert.Equal("alpha", RowText(screen, 1, 0, 5));
    }

    [Fact]
    public void The_caret_sits_on_its_glyph_not_in_the_gutter()
    {
        (EditorView view, _, _, Cursor cursor, _) = Setup();
        cursor.MoveTo(0, 3);

        view.Render();
        int without = view.CursorX;

        view.ShowLineNumbers = true;
        view.Render();

        Assert.Equal(3, without);
        Assert.Equal(3 + view.GutterWidth, view.CursorX);
    }

    [Fact]
    public void Horizontally_scrolled_text_still_starts_after_the_gutter()
    {
        (EditorView view, Screen screen, _, _, Viewport viewport) = Setup(Width, "0123456789abcdef");
        view.ShowLineNumbers = true;
        viewport.ScrollColumns(4);

        view.Render();

        // Column 4 of the line is the first thing painted, and it lands immediately
        // after the gutter rather than at screen column 0.
        Assert.Equal("1 456789", RowText(screen, 1, 0, 8));
    }

    [Fact]
    public void The_gutter_is_blank_past_the_end_of_the_buffer()
    {
        (EditorView view, Screen screen, _, _, _) = Setup();
        view.ShowLineNumbers = true;
        view.Render();

        // Row 4 is the first past the 3-line buffer, and still inside the text body.
        Assert.Equal("  ", RowText(screen, 4, 0, 2));
    }
}
