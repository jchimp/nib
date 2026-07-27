using Nib.Model;
using Xunit;

namespace Nib.Tests;

public class CursorTests
{
    private static TextBuffer Buffer(params string[] lines)
    {
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        return new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
    }

    [Fact]
    public void Desired_column_survives_a_short_line_and_returns()
    {
        // The acceptance behaviour: hold Down through a short line, the column comes back.
        var b = Buffer("0123456789", "ab", "0123456789");
        var c = new Cursor(b);
        c.MoveTo(0, 6);           // column 6 on the long first line
        c.Down();                 // onto "ab" — clamps to end (col 2)
        Assert.Equal(1, c.Row);
        Assert.Equal(2, c.Col);
        c.Down();                 // onto the long line again — column 6 restored
        Assert.Equal(2, c.Row);
        Assert.Equal(6, c.Col);
    }

    [Fact]
    public void Left_at_column_zero_wraps_to_previous_line_end()
    {
        var b = Buffer("abc", "def");
        var c = new Cursor(b);
        c.MoveTo(1, 0);
        c.Left();
        Assert.Equal(0, c.Row);
        Assert.Equal(3, c.Col);
    }

    [Fact]
    public void Right_at_line_end_wraps_to_next_line_start()
    {
        var b = Buffer("abc", "def");
        var c = new Cursor(b);
        c.MoveTo(0, 3);
        c.Right();
        Assert.Equal(1, c.Row);
        Assert.Equal(0, c.Col);
    }

    [Fact]
    public void Home_and_End_move_within_the_line()
    {
        var b = Buffer("hello");
        var c = new Cursor(b);
        c.MoveTo(0, 2);
        c.End();
        Assert.Equal(5, c.Col);
        c.Home();
        Assert.Equal(0, c.Col);
    }

    [Fact]
    public void Movement_clamps_at_buffer_edges()
    {
        var b = Buffer("abc");
        var c = new Cursor(b);
        c.Up();   // already at top
        Assert.Equal(0, c.Row);
        c.Left(); // already at origin
        Assert.Equal(0, c.Col);
        c.MoveTo(0, 3);
        c.Right(); // already at last char of last line
        Assert.Equal(0, c.Row);
        Assert.Equal(3, c.Col);
    }

    [Fact]
    public void Desired_column_tracks_display_column_through_tabs()
    {
        // "\tX" — 'X' sits at display column 8. Moving down should land at display 8.
        var b = Buffer("\tX", "01234567890");
        var c = new Cursor(b, tabWidth: 8);
        c.MoveTo(0, 1);                 // on 'X', display column 8
        Assert.Equal(8, c.DesiredColumn);
        c.Down();
        Assert.Equal(8, c.Col);         // char index 8 on the plain line == display 8
    }

    [Fact]
    public void WordRight_and_WordLeft_step_over_words()
    {
        var b = Buffer("foo bar baz");
        var c = new Cursor(b);
        c.WordRight();
        Assert.Equal(4, c.Col); // start of "bar"
        c.WordRight();
        Assert.Equal(8, c.Col); // start of "baz"
        c.WordLeft();
        Assert.Equal(4, c.Col); // back to start of "bar"
    }
}
