using Nib.Terminal;
using Xunit;

namespace Nib.Tests;

public class ScreenDiffTests
{
    // Render into a console-free writer and read back the VT it would have sent.
    private static string Render(Screen s)
    {
        var w = new TerminalWriter();
        s.Render(w);
        return w.DebugSnapshot();
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void First_render_emits_all_cell_content()
    {
        var s = new Screen(3, 1);
        s.Clear(Cell.Blank);
        s.PutText(0, 0, "abc", Color.Default, Color.Default);

        string vt = Render(s);

        Assert.Contains("abc", vt);
        Assert.Equal(1, Count(vt, "\x1b[1;1H")); // one move to the run start
    }

    [Fact]
    public void Identical_back_grid_emits_nothing_on_the_next_frame()
    {
        var s = new Screen(3, 1);
        s.Clear(Cell.Blank);
        s.PutText(0, 0, "abc", Color.Default, Color.Default);
        Render(s); // full first paint

        // Nothing repainted into the back grid, so the diff is empty.
        Assert.Equal("", Render(s));
    }

    [Fact]
    public void Single_changed_cell_emits_one_move_and_one_char()
    {
        var s = new Screen(5, 1);
        s.Clear(Cell.Blank);
        s.PutText(0, 0, "hello", Color.Default, Color.Default);
        Render(s);

        s.Set(2, 0, 'X', Color.Default, Color.Default); // change just column 2

        string vt = Render(s);
        Assert.Equal("\x1b[1;3HX", vt); // move to row 1 col 3, then the single char
    }

    [Fact]
    public void A_run_of_one_colour_emits_a_single_sgr_pair()
    {
        var s = new Screen(5, 1);
        s.Clear(Cell.Blank);
        var red = Color.Rgb(200, 0, 0);
        s.PutText(0, 0, "AB", red, Color.Default); // two cells, same colour

        string vt = Render(s);
        Assert.Equal(1, Count(vt, "\x1b[38;2;200;0;0m")); // fg emitted once, not per cell
    }

    [Fact]
    public void A_colour_change_mid_row_emits_a_new_sgr_at_the_boundary()
    {
        var s = new Screen(4, 1);
        s.Clear(Cell.Blank);
        s.PutText(0, 0, "AA", Color.Rgb(200, 0, 0), Color.Default);
        s.PutText(2, 0, "BB", Color.Rgb(0, 0, 200), Color.Default);

        string vt = Render(s);
        Assert.Equal(2, Count(vt, "\x1b[38;2;")); // one per colour, at the boundary only
    }

    [Fact]
    public void Resize_forces_a_full_repaint_even_when_content_is_identical()
    {
        var s = new Screen(3, 1);
        s.Clear(Cell.Blank);
        s.PutText(0, 0, "abc", Color.Default, Color.Default);
        Render(s);
        Assert.Equal("", Render(s)); // baseline: no-op frame is empty

        s.Resize(3, 1); // same dimensions, but the front grid is no longer trusted
        s.Clear(Cell.Blank);
        s.PutText(0, 0, "abc", Color.Default, Color.Default);

        Assert.Contains("abc", Render(s)); // repainted despite identical content
    }

    [Fact]
    public void Rendering_never_emits_a_newline_even_at_the_bottom_right_cell()
    {
        var s = new Screen(3, 2);
        s.Clear(Cell.Blank);
        s.PutText(0, 0, "xyz", Color.Default, Color.Default);
        s.Set(2, 1, 'Q', Color.Default, Color.Default); // bottom-right cell

        string vt = Render(s);
        Assert.DoesNotContain('\n', vt);
        Assert.DoesNotContain('\r', vt);
    }
}
