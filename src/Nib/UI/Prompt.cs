namespace Nib.Ui;

/// <summary>
/// A one-line modal text entry shown on the message row: "Save as: …" and the
/// like. Holds only its own tiny edit state; the editor loop drives it, feeding
/// keystrokes in and reading <see cref="Input"/> out. Esc-to-cancel and
/// Enter-to-accept are the loop's job, not this class's.
/// </summary>
public sealed class Prompt
{
    /// <summary>
    /// Settable, because the search prompt rewrites it mid-entry: Alt+C and Alt+W
    /// toggle case and whole-word while you are typing the term, and the badges in
    /// the label are the only place that state is visible.
    /// </summary>
    public string Label { get; set; }
    public string Input { get; private set; }
    public int Caret { get; private set; }

    /// <summary>
    /// A short report painted after the input, in its own colour: "wrapped",
    /// "Not found: foo". The find prompt stays open across matches, and
    /// <see cref="EditorView.ActivePrompt"/> displaces <see cref="EditorView.Message"/>
    /// while it is up — so without this there is nowhere for a search to say what
    /// happened until the prompt closes, by which time the answer is stale.
    /// </summary>
    public string Status { get; private set; } = "";

    public MessageKind StatusKind { get; private set; } = MessageKind.Info;

    public void SetStatus(string text, MessageKind kind = MessageKind.Info)
    {
        Status = text;
        StatusKind = kind;
    }

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
