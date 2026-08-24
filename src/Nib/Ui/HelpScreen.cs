namespace Nib.Ui;

/// <summary>
/// The full keymap, shown by ^H over the buffer and dismissed by any key.
///
/// It exists because search is the point where the one-line hint stopped fitting.
/// Both help rows and the old hint were within a few characters of 80 columns, and
/// ^F plus ^R do not fit on any of the three — the previous time that budget was
/// exceeded, the hint ran to 137 characters and its second half was clipped off the
/// right edge where nobody ever read it. Two rows now carry the keys used hourly and
/// this carries everything.
///
/// The text is a static array rather than something built at paint time so
/// <c>HelpScreenTests</c> can pin every line against 80 columns, the same guard
/// <c>HelpBarTests</c> puts on the rows.
/// </summary>
public static class HelpScreen
{
    public const string Title = "Nib — keys";
    public const string Footer = "Press any key to return";

    public static readonly string[] Lines =
    {
        "File",
        "  ^S  Save                        ^O  Save as",
        "  ^Q  Exit                        ^X  Cut selection, or exit if none",
        "",
        "Edit and clipboard",
        "  ^C  Copy                        ^V  Paste",
        "  ^X  Cut selection               ^A  Select all",
        "  ^K  Cut line                    ^U  Paste line",
        "  ^Z  Undo                        ^Y  Redo",
        "",
        "Search",
        "  ^F  Find — Enter next, Esc done   ^R  Replace, in selection if any",
        "  F3  Find next                       Shift+F3  Find previous",
        "  Alt+C  Case sensitive               Alt+W  Whole word  (both in the prompt)",
        "",
        "Move and select",
        "  Arrows  Home  End  PgUp  PgDn        ^Home / ^End  Start / end of file",
        "  ^Left / ^Right  A word at a time     ^G  Go to line",
        "  Shift with any movement key extends the selection; Esc clears it",
        "",
        "Display and counts",
        "  Alt+T  Cycle theme       Alt+N  Line numbers       ^W  Word count",
    };
}
