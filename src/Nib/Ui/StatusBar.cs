using Nib.Model;

namespace Nib.Ui;

/// <summary>
/// Builds the top title/status line text: name, modified marker, caret position,
/// encoding, and line-ending style. Pure string assembly so it is trivially
/// checkable; <see cref="EditorView"/> paints the result.
/// </summary>
public static class StatusBar
{
    public static string Build(TextBuffer buffer, Cursor cursor)
    {
        string name = buffer.Name ?? "(new)";
        string modified = buffer.IsModified ? "*" : "";
        string ending = EndingLabel(buffer.LineAt(cursor.Row).Ending, buffer.DefaultEnding);
        // Column is 1-based and display-based, matching what the user sees.
        return $"nib  {name}{modified}   L{cursor.Row + 1} C{cursor.DisplayColumn + 1}"
             + $"   {buffer.Encoding.DisplayName}  {ending}";
    }

    // The caret's own line dictates the label; a None-terminated final line reports
    // the buffer's dominant style so the status doesn't read blank at end of file.
    private static string EndingLabel(LineEnding ending, LineEnding fallback) => ending switch
    {
        LineEnding.Lf => "LF",
        LineEnding.CrLf => "CRLF",
        LineEnding.Cr => "CR",
        _ => fallback switch { LineEnding.CrLf => "CRLF", LineEnding.Cr => "CR", _ => "LF" },
    };
}
