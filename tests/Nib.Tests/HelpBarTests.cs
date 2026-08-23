using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The shortcut rows and the ^H hint all have to survive an 80-column window.
/// EditorView clips them silently, so going over does not fail, break or warn - the
/// keys past column 80 simply stop existing as far as the user can tell. That is how
/// the old hint got to 137 characters with its second half never once read.
/// </summary>
public class HelpBarTests
{
    private const int NarrowestSupportedWindow = 80;

    [Theory]
    [InlineData("Row1")]
    [InlineData("Row2")]
    [InlineData("Hint")]
    public void Fits_an_eighty_column_window(string which)
    {
        string text = which switch
        {
            "Row1" => HelpBar.Row1,
            "Row2" => HelpBar.Row2,
            _ => HelpBar.Hint,
        };

        // The hint is padded by one cell either side on the message row.
        int painted = which == "Hint" ? text.Length + 2 : text.Length;

        Assert.True(
            painted <= NarrowestSupportedWindow,
            $"{which} paints {painted} columns; over {NarrowestSupportedWindow} it is clipped unseen. Take a key out.");
    }

    [Fact]
    public void The_hint_does_not_restate_what_the_rows_already_show()
    {
        // Its whole job is the keys the rows have no room for.
        foreach (string chord in new[] { "^C", "^X", "^V", "^Z", "^Y", "^K", "^U", "^S", "^O", "^Q" })
            Assert.DoesNotContain(chord, HelpBar.Hint, System.StringComparison.Ordinal);

        Assert.Contains("Alt+T", HelpBar.Hint, System.StringComparison.Ordinal);
        Assert.Contains("Alt+N", HelpBar.Hint, System.StringComparison.Ordinal);
    }
}
