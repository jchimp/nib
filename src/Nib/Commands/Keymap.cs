using Nib.Terminal;

namespace Nib.Commands;

/// <summary>
/// Actions the keymap cannot complete on its own because they need the console
/// or a modal prompt. Everything else (typing, navigation, in-buffer edits) is
/// done inline and reported as <see cref="None"/>.
/// </summary>
public enum EditorAction
{
    None,
    Save,
    Quit,
    Help,
    GoToLine,
    CycleTheme,
}

/// <summary>
/// Translates a key event into an edit/movement (applied immediately through
/// <see cref="EditorCommands"/>) or an <see cref="EditorAction"/> for the loop to
/// handle. Phase 4 adds selection (Shift+movement, Ctrl+Shift+word), the CUA
/// clipboard chords, undo/redo, and nano's ^K/^U — so Ctrl+X now means *cut* when
/// there is a selection and quit only when there isn't.
/// </summary>
public static class Keymap
{
    public static EditorAction Handle(InputEvent ev, EditorCommands cmd, int pageRows)
    {
        if (ev.Kind != InputEventKind.Key) return EditorAction.None;

        Model.Cursor cur = cmd.Cursor;
        bool ext = ev.Shift; // Shift held → extend the selection through the move

        // Alt+T cycles the theme. Alt, not Ctrl: every Ctrl chord is already spoken
        // for by the nano and CUA bindings, and taking one for a cosmetic toggle
        // would cost muscle memory that matters more.
        if (ev.Alt) return ev.Key == ConsoleKey.T ? EditorAction.CycleTheme : EditorAction.None;

        if (ev.Ctrl)
        {
            switch (ev.Key)
            {
                case ConsoleKey.S:
                case ConsoleKey.O: return EditorAction.Save;

                case ConsoleKey.G: return EditorAction.GoToLine;

                // Ctrl+H, not Backspace. InputReader keys off wVirtualKeyCode, so this
                // is VK_H (0x48) and the Backspace key is VK_BACK (0x08) — two distinct
                // events, and the 0x08 control code Ctrl+H also produces in UnicodeChar
                // is blanked there. The Backspace case below is in the non-Ctrl switch
                // and is untouched. Do not "disambiguate" these.
                case ConsoleKey.H: return EditorAction.Help;

                // Ctrl+Q always quits. Ctrl+X keeps nano's context-dependent meaning —
                // cut a selection, quit without one — so both habits work.
                case ConsoleKey.Q: return EditorAction.Quit;

                case ConsoleKey.X:
                    if (cmd.HasSelection) { cmd.Cut(); return EditorAction.None; }
                    return EditorAction.Quit;

                case ConsoleKey.C: cmd.Copy(); break;
                case ConsoleKey.V: cmd.Paste(); break;
                case ConsoleKey.A: cmd.SelectAll(); break;
                case ConsoleKey.K: cmd.CutLine(); break;
                case ConsoleKey.U: cmd.PasteLine(); break;
                case ConsoleKey.Z: cmd.Undo(); break;
                case ConsoleKey.Y: cmd.Redo(); break;

                // Ctrl (+Shift) navigation.
                case ConsoleKey.Home: cmd.Move(cur.DocumentStart, ext); break;
                case ConsoleKey.End: cmd.Move(cur.DocumentEnd, ext); break;
                case ConsoleKey.LeftArrow: cmd.Move(cur.WordLeft, ext); break;
                case ConsoleKey.RightArrow: cmd.Move(cur.WordRight, ext); break;
            }
            return EditorAction.None;
        }

        switch (ev.Key)
        {
            case ConsoleKey.UpArrow: cmd.Move(cur.Up, ext); return EditorAction.None;
            case ConsoleKey.DownArrow: cmd.Move(cur.Down, ext); return EditorAction.None;
            case ConsoleKey.LeftArrow: cmd.Move(cur.Left, ext); return EditorAction.None;
            case ConsoleKey.RightArrow: cmd.Move(cur.Right, ext); return EditorAction.None;
            case ConsoleKey.Home: cmd.Move(cur.Home, ext); return EditorAction.None;
            case ConsoleKey.End: cmd.Move(cur.End, ext); return EditorAction.None;
            case ConsoleKey.PageUp: cmd.Move(() => cmd.PageUp(pageRows), ext); return EditorAction.None;
            case ConsoleKey.PageDown: cmd.Move(() => cmd.PageDown(pageRows), ext); return EditorAction.None;
            case ConsoleKey.Enter: cmd.Enter(); return EditorAction.None;
            case ConsoleKey.Backspace: cmd.Backspace(); return EditorAction.None;
            case ConsoleKey.Delete: cmd.Delete(); return EditorAction.None;
            case ConsoleKey.Tab: cmd.InsertChar('\t'); return EditorAction.None;

            // Esc collapses a selection. Until now only an unshifted move did, so a
            // selection the user wanted rid of without moving the caret had no key —
            // which is the whole reason copy now drops it too.
            case ConsoleKey.Escape: cmd.ClearSelection(); return EditorAction.None;
        }

        // Printable text: astral pairs arrive as Text, everything else as Char.
        if (ev.Text is { Length: > 0 }) cmd.Insert(ev.Text);
        else if (ev.Char >= ' ' && ev.Char != '\x7f') cmd.InsertChar(ev.Char);

        return EditorAction.None;
    }
}
