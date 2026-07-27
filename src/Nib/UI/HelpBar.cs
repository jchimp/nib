namespace Nib.Ui;

/// <summary>
/// The two nano-style shortcut rows at the bottom. Only phase-3 actions are live;
/// Find and GoTo are shown because their keys are reserved (they land in phase 6),
/// matching nano's habit of always showing the full bar.
/// </summary>
public static class HelpBar
{
    public static readonly string Row1 = "^S Save   ^O SaveAs   ^X Exit";
    public static readonly string Row2 = "^G Help   ^L GoTo     ^W Find";
}
