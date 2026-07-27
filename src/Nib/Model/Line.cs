namespace Nib.Model;

/// <summary>
/// One line of the buffer: its text (without the terminator) and the terminator
/// that followed it in the source. Mutable — edits rewrite <see cref="Text"/> in
/// place; a split or join rewrites <see cref="Ending"/>.
/// </summary>
public sealed class Line
{
    public string Text { get; set; }
    public LineEnding Ending { get; set; }

    public Line(string text, LineEnding ending)
    {
        Text = text;
        Ending = ending;
    }
}
