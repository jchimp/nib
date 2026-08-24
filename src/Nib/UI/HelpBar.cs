namespace Nib.Ui;

/// <summary>
/// The two nano-style shortcut rows at the bottom. ^X is context-dependent — it
/// cuts a selection and quits when there is none — hence "Cut/Exit"; ^Q always
/// exits.
///
/// Both rows are kept under 80 columns (75 and 69) so an 80-column window does not
/// truncate them mid-word. Adding a key means taking one out — which is what
/// happened here: ^F and ^R arrived with phase 6c and pushed ^A and ^U off the
/// rows. They are not lost, they are on <see cref="HelpScreen"/>, which is what ^H
/// now opens and why there is no longer a one-line hint trying to be a keymap.
/// </summary>
public static class HelpBar
{
    public static readonly string Row1 = "^H Help   ^F Find   ^R Replace   ^C Copy   ^X Cut/Exit   ^V Paste   ^Q Exit";
    public static readonly string Row2 = "^S Save   ^O SaveAs   ^G Go to line   ^K Cut line   ^Z Undo   ^Y Redo";
}
