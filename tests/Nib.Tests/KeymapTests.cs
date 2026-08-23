using Nib.Commands;
using Nib.Model;
using Nib.Terminal;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The chord table itself. Until now it was only ever tested through its effects,
/// which is fine for the verbs and useless for the two things that actually broke
/// people's fingers: which key exits, and whether Ctrl+H is Backspace.
///
/// It is not: <see cref="InputReader"/> keys off wVirtualKeyCode, so Ctrl+H arrives
/// as <c>ConsoleKey.H</c> and the Backspace key as <c>ConsoleKey.Backspace</c>.
/// </summary>
public class KeymapTests
{
    private sealed class FakeClipboard : IClipboard
    {
        public string Text = "";
        public string GetText() => Text;
        public bool SetText(string text) { Text = text; return true; }
    }

    private static (EditorCommands Cmd, TextBuffer Buf, Cursor Cur, FakeClipboard Clip) Setup(params string[] lines)
    {
        if (lines.Length == 0) lines = ["hello world"];
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        var buffer = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
        var cursor = new Cursor(buffer);
        var clip = new FakeClipboard();
        return (new EditorCommands(buffer, cursor, clip), buffer, cursor, clip);
    }

    private static InputEvent Key(ConsoleKey key, KeyModifiers mods = KeyModifiers.None) =>
        new() { Kind = InputEventKind.Key, Key = key, Modifiers = mods };

    private static InputEvent Ctrl(ConsoleKey key) => Key(key, KeyModifiers.Control);

    private static void Select(EditorCommands cmd, Cursor cur, int r0, int c0, int r1, int c1)
    {
        cur.MoveTo(r0, c0);
        cmd.Move(() => { }, extend: true);
        cur.MoveTo(r1, c1);
    }

    private static EditorAction Send(InputEvent ev, EditorCommands cmd) => Keymap.Handle(ev, cmd, pageRows: 10);

    [Fact]
    public void Ctrl_Q_quits_whether_or_not_there_is_a_selection()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, _) = Setup();
        Assert.Equal(EditorAction.Quit, Send(Ctrl(ConsoleKey.Q), cmd));

        Select(cmd, cur, 0, 0, 0, 5);
        Assert.Equal(EditorAction.Quit, Send(Ctrl(ConsoleKey.Q), cmd));
        Assert.Equal("hello world", buf.GetLine(0)); // and it did not cut on the way out
    }

    [Fact]
    public void Ctrl_X_still_cuts_a_selection_and_quits_without_one()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup();
        Select(cmd, cur, 0, 0, 0, 6);

        Assert.Equal(EditorAction.None, Send(Ctrl(ConsoleKey.X), cmd));
        Assert.Equal("world", buf.GetLine(0));
        Assert.Equal("hello ", clip.Text);

        Assert.Equal(EditorAction.Quit, Send(Ctrl(ConsoleKey.X), cmd));
    }

    [Fact]
    public void Ctrl_C_copies_and_never_quits()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup();
        Select(cmd, cur, 0, 0, 0, 5);

        Assert.Equal(EditorAction.None, Send(Ctrl(ConsoleKey.C), cmd));
        Assert.Equal("hello", clip.Text);
        Assert.Equal("hello world", buf.GetLine(0));

        // With nothing selected it is still not an exit.
        Assert.Equal(EditorAction.None, Send(Ctrl(ConsoleKey.C), cmd));
    }

    [Fact]
    public void Ctrl_G_goes_to_line_and_Ctrl_H_helps()
    {
        (EditorCommands cmd, _, _, _) = Setup();
        Assert.Equal(EditorAction.GoToLine, Send(Ctrl(ConsoleKey.G), cmd));
        Assert.Equal(EditorAction.Help, Send(Ctrl(ConsoleKey.H), cmd));
    }

    [Fact]
    public void Ctrl_H_does_not_delete_and_Backspace_still_does()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, _) = Setup();
        cur.MoveTo(0, 5);

        Send(Ctrl(ConsoleKey.H), cmd);
        Assert.Equal("hello world", buf.GetLine(0));

        Send(Key(ConsoleKey.Backspace), cmd);
        Assert.Equal("hell world", buf.GetLine(0));
    }

    [Fact]
    public void Alt_chords_are_the_view_toggles_and_nothing_else()
    {
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup();

        Assert.Equal(EditorAction.CycleTheme, Send(Key(ConsoleKey.T, KeyModifiers.Alt), cmd));
        Assert.Equal(EditorAction.ToggleLineNumbers, Send(Key(ConsoleKey.N, KeyModifiers.Alt), cmd));

        // An unbound Alt chord must not fall through and type its letter.
        Assert.Equal(EditorAction.None, Send(Key(ConsoleKey.J, KeyModifiers.Alt), cmd));
        Assert.Equal("hello world", buf.GetLine(0));
    }

    [Fact]
    public void Escape_collapses_a_selection_without_moving_the_caret()
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup();
        Select(cmd, cur, 0, 0, 0, 5);
        Assert.True(cmd.HasSelection);

        Assert.Equal(EditorAction.None, Send(Key(ConsoleKey.Escape), cmd));
        Assert.False(cmd.HasSelection);
        Assert.Equal(0, cur.Row);
        Assert.Equal(5, cur.Col);
    }

    [Fact]
    public void Escape_does_not_type_a_character()
    {
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup();
        Send(Key(ConsoleKey.Escape), cmd);
        Assert.Equal("hello world", buf.GetLine(0));
    }
}
