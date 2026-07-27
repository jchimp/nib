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
}

/// <summary>
/// Translates a key event into an edit/movement (applied immediately through
/// <see cref="EditorCommands"/>) or an <see cref="EditorAction"/> for the loop to
/// handle. Phase-3 bindings only: selection, clipboard, and undo (Ctrl+C/V/K/U/Z)
/// are deliberately swallowed here until phase 4, and Ctrl+X always means quit.
/// </summary>
public static class Keymap
{
    public static EditorAction Handle(InputEvent ev, EditorCommands cmd, int pageRows)
    {
        if (ev.Kind != InputEventKind.Key) return EditorAction.None;

        Model.Cursor cur = cmd.Cursor;

        if (ev.Alt) return EditorAction.None; // no Alt chords in phase 3

        if (ev.Ctrl)
        {
            switch (ev.Key)
            {
                case ConsoleKey.S:
                case ConsoleKey.O: return EditorAction.Save;
                case ConsoleKey.X: return EditorAction.Quit;
                case ConsoleKey.G: return EditorAction.Help;
                case ConsoleKey.Home: cur.DocumentStart(); break;
                case ConsoleKey.End: cur.DocumentEnd(); break;
                case ConsoleKey.LeftArrow: cur.WordLeft(); break;
                case ConsoleKey.RightArrow: cur.WordRight(); break;
                // Other Ctrl combos are phase-4 keys; swallow them for now.
            }
            return EditorAction.None;
        }

        switch (ev.Key)
        {
            case ConsoleKey.UpArrow: cur.Up(); return EditorAction.None;
            case ConsoleKey.DownArrow: cur.Down(); return EditorAction.None;
            case ConsoleKey.LeftArrow: cur.Left(); return EditorAction.None;
            case ConsoleKey.RightArrow: cur.Right(); return EditorAction.None;
            case ConsoleKey.Home: cur.Home(); return EditorAction.None;
            case ConsoleKey.End: cur.End(); return EditorAction.None;
            case ConsoleKey.PageUp: cmd.PageUp(pageRows); return EditorAction.None;
            case ConsoleKey.PageDown: cmd.PageDown(pageRows); return EditorAction.None;
            case ConsoleKey.Enter: cmd.Enter(); return EditorAction.None;
            case ConsoleKey.Backspace: cmd.Backspace(); return EditorAction.None;
            case ConsoleKey.Delete: cmd.Delete(); return EditorAction.None;
            case ConsoleKey.Tab: cmd.InsertChar('\t'); return EditorAction.None;
        }

        // Printable text: astral pairs arrive as Text, everything else as Char.
        if (ev.Text is { Length: > 0 }) cmd.Insert(ev.Text);
        else if (ev.Char >= ' ' && ev.Char != '\x7f') cmd.InsertChar(ev.Char);

        return EditorAction.None;
    }
}
