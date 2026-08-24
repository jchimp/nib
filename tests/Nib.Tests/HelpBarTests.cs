using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The shortcut rows and every line of the ^H screen have to survive an 80-column
/// window. EditorView clips them silently, so going over does not fail, break or
/// warn - the keys past column 80 simply stop existing as far as the user can tell.
/// That is how the old one-line hint got to 137 characters with its second half
/// never once read.
/// </summary>
public class HelpBarTests
{
    private const int NarrowestSupportedWindow = 80;

    [Theory]
    [InlineData("Row1")]
    [InlineData("Row2")]
    public void Fits_an_eighty_column_window(string which)
    {
        string text = which == "Row1" ? HelpBar.Row1 : HelpBar.Row2;

        Assert.True(
            text.Length <= NarrowestSupportedWindow,
            $"{which} paints {text.Length} columns; over {NarrowestSupportedWindow} it is clipped unseen. Take a key out.");
    }

    [Fact]
    public void The_rows_carry_the_keys_used_hourly_and_point_at_the_rest()
    {
        string both = HelpBar.Row1 + HelpBar.Row2;

        // Search is a first-class key now, not something to discover in the docs.
        Assert.Contains("^F", both, System.StringComparison.Ordinal);
        Assert.Contains("^R", both, System.StringComparison.Ordinal);

        // And ^H has to be on the rows, because it is the only route to the keys
        // that were pushed off them to make the room.
        Assert.Contains("^H", both, System.StringComparison.Ordinal);
    }
}
