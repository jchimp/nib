using Nib.Ui;
using Xunit;

namespace Nib.Tests;

public class ViewportTests
{
    // ---- tab expansion ------------------------------------------------------

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("\t", 8)]              // a leading tab fills one full stop
    [InlineData("a\t", 8)]            // 'a' then tab advances to column 8
    [InlineData("ab\tc", 9)]         // tab from col 2 -> 8, then 'c' -> 9
    [InlineData("\t\t", 16)]          // two tabs, two stops
    public void DisplayWidth_expands_tabs_to_stops(string line, int expected)
    {
        var vp = new Viewport(tabWidth: 8);
        Assert.Equal(expected, vp.DisplayWidth(line));
    }

    [Fact]
    public void ExpandLine_replaces_tabs_with_spaces_to_next_stop()
    {
        var vp = new Viewport(tabWidth: 4);
        // "a" -> col 1, tab -> col 4 (3 spaces), "b" -> col 5.
        Assert.Equal("a   b", vp.ExpandLine("a\tb"));
    }

    [Fact]
    public void ExpandLine_and_DisplayWidth_agree_on_mixed_tabs_and_spaces()
    {
        var vp = new Viewport(tabWidth: 8);
        const string line = "\tif x:\t# note";
        Assert.Equal(vp.ExpandLine(line).Length, vp.DisplayWidth(line));
    }

    // ---- char <-> display column mapping ------------------------------------

    [Fact]
    public void CharToDisplayColumn_accounts_for_a_leading_tab()
    {
        var vp = new Viewport(tabWidth: 8);
        // "\tX": char 0 starts at col 0, char 1 ('X') starts at col 8.
        Assert.Equal(0, vp.CharToDisplayColumn("\tX", 0));
        Assert.Equal(8, vp.CharToDisplayColumn("\tX", 1));
    }

    [Fact]
    public void DisplayToCharColumn_maps_inside_a_tab_to_the_tab_index()
    {
        var vp = new Viewport(tabWidth: 8);
        // Columns 0..7 are all covered by the tab at char index 0.
        Assert.Equal(0, vp.DisplayToCharColumn("\tX", 0));
        Assert.Equal(0, vp.DisplayToCharColumn("\tX", 7));
        Assert.Equal(1, vp.DisplayToCharColumn("\tX", 8));
    }

    [Theory]
    [InlineData("\tif x = y\t;", 4)]
    [InlineData("no tabs here", 8)]
    [InlineData("lead\ttrail", 3)]
    public void Char_and_display_mappings_round_trip_at_char_boundaries(string line, int tabWidth)
    {
        var vp = new Viewport(tabWidth);
        // At every character boundary, display->char->display is identity.
        for (int i = 0; i <= line.Length; i++)
        {
            int disp = vp.CharToDisplayColumn(line, i);
            Assert.Equal(i, vp.DisplayToCharColumn(line, disp));
        }
    }

    // ---- clamping -----------------------------------------------------------

    [Fact]
    public void ClampVertical_cannot_scroll_past_last_line_or_negative()
    {
        var vp = new Viewport();
        vp.ScrollLines(1000);
        vp.ClampVertical(totalLines: 10);
        Assert.Equal(9, vp.FirstLine);

        vp.ScrollLines(-1000);
        vp.ClampVertical(totalLines: 10);
        Assert.Equal(0, vp.FirstLine);
    }

    [Fact]
    public void ClampVertical_on_empty_document_pins_to_zero()
    {
        var vp = new Viewport();
        vp.ScrollLines(5);
        vp.ClampVertical(totalLines: 0);
        Assert.Equal(0, vp.FirstLine);
    }

    [Fact]
    public void ClampHorizontal_cannot_scroll_past_widest_line_or_negative()
    {
        var vp = new Viewport();
        vp.ScrollColumns(500);
        vp.ClampHorizontal(maxDisplayWidth: 40);
        Assert.Equal(39, vp.FirstColumn);

        vp.ScrollColumns(-500);
        vp.ClampHorizontal(maxDisplayWidth: 40);
        Assert.Equal(0, vp.FirstColumn);
    }
}
