namespace Nib.Ui;

/// <summary>
/// The two nano-style shortcut rows at the bottom. ^X is context-dependent — it
/// cuts a selection and quits when there is none — hence "Cut/Exit"; ^Q always
/// exits. Find lands in a later phase and is dropped for now rather than shown as
/// a dead key.
///
/// Both rows are kept under 80 columns (72 and 74) so an 80-column window does not
/// truncate them mid-word. Adding a key means taking one out.
/// </summary>
public static class HelpBar
{
    public static readonly string Row1 = "^H Help   ^C Copy   ^X Cut/Exit   ^V Paste   ^Z Undo   ^Y Redo   ^Q Exit";
    public static readonly string Row2 = "^S Save   ^O SaveAs   ^G Go to line   ^K Cut line   ^U Paste line   ^A All";

    /// <summary>
    /// The ^H message-row hint: the keys the two rows above have no space for. It
    /// deliberately does not restate them - the previous version did, ran to 137
    /// characters, and had its second half clipped off the right edge where nobody
    /// ever read it. Same 80-column budget as the rows.
    /// </summary>
    public static readonly string Hint =
        "Select: Shift+arrows  Word: ^arrows  Clear: Esc  Theme: Alt+T  Numbers: Alt+N";
}
