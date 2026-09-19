using Nib.Model;
using Nib.Terminal;
using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// <see cref="EditorView.HitTest"/> is the inverse of the caret placement, and has
/// to agree with it about the title row, the gutter and the horizontal scroll —
/// otherwise a click lands one cell off from where the caret then paints.
/// </summary>
public class HitTestTests
{
    private const int Width = 80;
    private const int Height = 24; // 20 text rows (1..20), message at 21, help at 22-23

    private static (EditorView View, TextBuffer Buffer, Cursor Cursor, Viewport Viewport)
        Setup(params string[] lines)
    {
        if (lines.Length == 0) lines = ["alpha", "beta", "gamma"];
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        var buffer = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
        var screen = new Screen(Width, Height);
        var viewport = new Viewport();
        var cursor = new Cursor(buffer, viewport.TabWidth);
        var view = new EditorView(screen, viewport, buffer, cursor);
        return (view, buffer, cursor, viewport);
    }

    [Fact]
    public void Title_message_and_help_rows_are_not_text()
    {
        var (view, _, _, _) = Setup();
        Assert.Null(view.HitTest(5, 0));
        Assert.Null(view.HitTest(5, 21));
        Assert.Null(view.HitTest(5, 22));
        Assert.Null(view.HitTest(5, 23));
        Assert.NotNull(view.HitTest(5, 1));
        Assert.NotNull(view.HitTest(5, 20));
    }

    [Fact]
    public void First_text_row_is_screen_row_one()
    {
        var (view, _, _, _) = Setup();
        Assert.Equal(new TextPosition(0, 2), view.HitTest(2, 1));
        Assert.Equal(new TextPosition(1, 2), view.HitTest(2, 2));
    }

    [Fact]
    public void Past_end_of_line_lands_on_the_end()
    {
        var (view, _, _, _) = Setup("ab");
        Assert.Equal(new TextPosition(0, 2), view.HitTest(40, 1));
    }

    [Fact]
    public void Below_the_last_line_lands_on_the_last_line()
    {
        var (view, _, _, _) = Setup("one", "two");
        Assert.Equal(new TextPosition(1, 3), view.HitTest(10, 15));
    }

    [Fact]
    public void Gutter_takes_columns_off_the_left_and_a_gutter_click_is_column_zero()
    {
        var (view, _, _, _) = Setup("alpha", "beta", "gamma");
        view.ShowLineNumbers = true;
        int gutter = view.GutterWidth;
        Assert.True(gutter > 0);
        Assert.Equal(new TextPosition(0, 0), view.HitTest(0, 1));
        Assert.Equal(new TextPosition(0, 0), view.HitTest(gutter - 1, 1));
        Assert.Equal(new TextPosition(0, 3), view.HitTest(gutter + 3, 1));
    }

    [Fact]
    public void Vertical_and_horizontal_scroll_offset_the_hit()
    {
        var lines = new string[40];
        for (int i = 0; i < lines.Length; i++) lines[i] = new string('x', 200);
        var (view, _, cursor, viewport) = Setup(lines);
        cursor.MoveTo(30, 150);
        viewport.EnsureVisible(30, 150, view.TextRows, view.TextColumns);

        Assert.Equal(new TextPosition(viewport.FirstLine + 4, viewport.FirstColumn + 7), view.HitTest(7, 5));
    }

    [Fact]
    public void A_click_inside_a_tab_resolves_to_the_tab()
    {
        // "\tx": the tab spans display columns 0..7, x is at 8.
        var (view, _, _, _) = Setup("\tx");
        Assert.Equal(new TextPosition(0, 0), view.HitTest(0, 1));
        Assert.Equal(new TextPosition(0, 0), view.HitTest(5, 1));
        Assert.Equal(new TextPosition(0, 1), view.HitTest(8, 1));
    }

    [Fact]
    public void Clamped_variant_pulls_off_area_rows_to_the_nearest_edge()
    {
        var (view, _, _, _) = Setup("one", "two", "three");
        Assert.Equal(new TextPosition(0, 1), view.HitTestClamped(1, 0));   // title row → first text row
        Assert.Equal(new TextPosition(2, 1), view.HitTestClamped(1, 23));  // help row → last line
    }
}
