namespace Nib.Ui;

/// <summary>
/// The two nano-style shortcut rows at the bottom. ^X is context-dependent — it
/// cuts a selection and quits when there is none — hence "Cut/Exit". Find and GoTo
/// land in a later phase and are dropped for now rather than shown as dead keys.
/// </summary>
public static class HelpBar
{
    public static readonly string Row1 = "^G Help   ^C Copy   ^X Cut/Exit   ^V Paste   ^Z Undo   ^Y Redo";
    public static readonly string Row2 = "^S Save   ^O SaveAs   ^K Cut line   ^U Paste line   ^A Select all";
}
