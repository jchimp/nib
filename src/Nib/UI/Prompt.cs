namespace Nib.Ui;

/// <summary>
/// A one-line modal text entry shown on the message row: "Save as: …" and the
/// like. Holds only its own tiny edit state; the editor loop drives it, feeding
/// keystrokes in and reading <see cref="Input"/> out. Esc-to-cancel and
/// Enter-to-accept are the loop's job, not this class's.
/// </summary>
public sealed class Prompt
{
    public string Label { get; }
    public string Input { get; private set; }
    public int Caret { get; private set; }

    public Prompt(string label, string initial = "")
    {
        Label = label;
        Input = initial;
        Caret = initial.Length;
    }

    public void InsertText(string text)
    {
        Input = Input.Insert(Caret, text);
        Caret += text.Length;
    }

    public void Backspace()
    {
        if (Caret == 0) return;
        Input = Input.Remove(Caret - 1, 1);
        Caret--;
    }

    public void Delete()
    {
        if (Caret >= Input.Length) return;
        Input = Input.Remove(Caret, 1);
    }

    public void Left() => Caret = Math.Max(0, Caret - 1);
    public void Right() => Caret = Math.Min(Input.Length, Caret + 1);
    public void Home() => Caret = 0;
    public void End() => Caret = Input.Length;
}
