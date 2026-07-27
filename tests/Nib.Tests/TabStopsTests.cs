using Nib.Model;
using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The tab math moved from Viewport into Model/TabStops so the cursor could share
/// it. These pin the extracted functions directly; ViewportTests still cover the
/// delegating wrapper.
/// </summary>
public class TabStopsTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("\t", 8)]
    [InlineData("a\t", 8)]
    [InlineData("ab\tc", 9)]
    public void DisplayWidth_expands_tabs_to_stops(string line, int expected)
        => Assert.Equal(expected, TabStops.DisplayWidth(line, tabWidth: 8));

    [Fact]
    public void Char_and_display_mappings_round_trip_at_boundaries()
    {
        const string line = "\tif x = y\t;";
        for (int i = 0; i <= line.Length; i++)
        {
            int disp = TabStops.CharToDisplayColumn(line, i, 4);
            Assert.Equal(i, TabStops.DisplayToCharColumn(line, disp, 4));
        }
    }

    [Fact]
    public void ExpandLine_matches_DisplayWidth()
    {
        const string line = "\tif x:\t# note";
        Assert.Equal(TabStops.ExpandLine(line, 8).Length, TabStops.DisplayWidth(line, 8));
    }
}

public class ViewportScrollTests
{
    [Fact]
    public void EnsureVisible_scrolls_down_to_reveal_the_cursor()
    {
        var vp = new Viewport();
        vp.EnsureVisible(cursorLine: 30, cursorDisplayColumn: 0, textRows: 10, textColumns: 80);
        // Cursor must be the last visible row: FirstLine = 30 - 10 + 1.
        Assert.Equal(21, vp.FirstLine);
    }

    [Fact]
    public void EnsureVisible_scrolls_up_to_reveal_the_cursor()
    {
        var vp = new Viewport();
        vp.ScrollLines(20);
        vp.EnsureVisible(cursorLine: 5, cursorDisplayColumn: 0, textRows: 10, textColumns: 80);
        Assert.Equal(5, vp.FirstLine);
    }

    [Fact]
    public void EnsureVisible_scrolls_horizontally_both_ways()
    {
        var vp = new Viewport();
        vp.EnsureVisible(cursorLine: 0, cursorDisplayColumn: 120, textRows: 10, textColumns: 80);
        Assert.Equal(41, vp.FirstColumn); // 120 - 80 + 1

        vp.EnsureVisible(cursorLine: 0, cursorDisplayColumn: 10, textRows: 10, textColumns: 80);
        Assert.Equal(10, vp.FirstColumn);
    }

    [Fact]
    public void EnsureVisible_does_not_move_when_cursor_already_shown()
    {
        var vp = new Viewport();
        vp.ScrollLines(5);
        vp.EnsureVisible(cursorLine: 8, cursorDisplayColumn: 3, textRows: 10, textColumns: 80);
        Assert.Equal(5, vp.FirstLine);
        Assert.Equal(0, vp.FirstColumn);
    }
}
