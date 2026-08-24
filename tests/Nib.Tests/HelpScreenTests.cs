using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The ^H screen is the only place several keys are documented at all, now that ^A
/// and ^U have been pushed off the two rows. Both ways it can silently lose a key
/// are pinned here: a line running past column 80, and the page running past the
/// bottom of a 24-row window. RenderOverlay clips in both directions without
/// complaint, so neither shows up as a failure anywhere else.
/// </summary>
public class HelpScreenTests
{
    private const int NarrowestSupportedWindow = 80;
    private const int ShortestSupportedWindow = 24;

    [Fact]
    public void Every_line_fits_an_eighty_column_window()
    {
        foreach (string line in HelpScreen.Lines)
            Assert.True(
                line.Length <= NarrowestSupportedWindow,
                $"\"{line}\" paints {line.Length} columns; over {NarrowestSupportedWindow} it is clipped unseen.");

        Assert.True(HelpScreen.Title.Length <= NarrowestSupportedWindow);
        Assert.True(HelpScreen.Footer.Length <= NarrowestSupportedWindow);
    }

    [Fact]
    public void The_whole_page_fits_a_twenty_four_row_window()
    {
        // A title bar at the top and a footer bar at the bottom; the rest is the page.
        int available = ShortestSupportedWindow - 2;

        Assert.True(
            HelpScreen.Lines.Length <= available,
            $"{HelpScreen.Lines.Length} lines against {available} rows; the overflow is clipped, not scrolled.");
    }

    [Theory]
    [InlineData("^S")]
    [InlineData("^O")]
    [InlineData("^Q")]
    [InlineData("^X")]
    [InlineData("^C")]
    [InlineData("^V")]
    [InlineData("^A")]
    [InlineData("^K")]
    [InlineData("^U")]
    [InlineData("^Z")]
    [InlineData("^Y")]
    [InlineData("^G")]
    [InlineData("^F")]
    [InlineData("^R")]
    [InlineData("F3")]
    [InlineData("Alt+T")]
    [InlineData("Alt+N")]
    [InlineData("Alt+C")]
    [InlineData("Alt+W")]
    public void Documents_every_bound_key(string chord)
    {
        string page = string.Join("\n", HelpScreen.Lines);
        Assert.Contains(chord, page, System.StringComparison.Ordinal);
    }
}
