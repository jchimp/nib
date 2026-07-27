using Nib.Commands;
using Nib.Model;
using Xunit;

namespace Nib.Tests;

public class EditorCommandsTests
{
    private sealed class FakeClipboard : IClipboard
    {
        public string Text = "";
        public string GetText() => Text;
        public bool SetText(string text) { Text = text; return true; }
    }

    private static (EditorCommands Cmd, TextBuffer Buf, Cursor Cur, FakeClipboard Clip) Setup(params string[] lines)
    {
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        var buffer = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
        var cursor = new Cursor(buffer);
        var clip = new FakeClipboard();
        return (new EditorCommands(buffer, cursor, clip), buffer, cursor, clip);
    }

    // Select from (row0,col0) to (row1,col1) by anchoring then moving the cursor.
    private static void Select(EditorCommands cmd, Cursor cur, int r0, int c0, int r1, int c1)
    {
        cur.MoveTo(r0, c0);
        cmd.Move(() => { }, extend: true); // drop the anchor here
        cur.MoveTo(r1, c1);
    }

    [Fact]
    public void Cut_removes_the_selection_and_populates_the_clipboard()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup("hello world");
        Select(cmd, cur, 0, 0, 0, 5);
        cmd.Cut();
        Assert.Equal("hello", clip.Text);
        Assert.Equal(" world", buf.GetLine(0));
        Assert.False(cmd.HasSelection);
    }

    [Fact]
    public void Copy_leaves_the_buffer_untouched()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup("hello world");
        Select(cmd, cur, 0, 6, 0, 11);
        cmd.Copy();
        Assert.Equal("world", clip.Text);
        Assert.Equal("hello world", buf.GetLine(0));
    }

    [Fact]
    public void Copy_normalizes_multiline_text_to_CRLF_for_windows_apps()
    {
        (EditorCommands cmd, TextBuffer _, Cursor cur, FakeClipboard clip) = Setup("foo", "bar");
        Select(cmd, cur, 0, 0, 1, 3);
        cmd.Copy();
        Assert.Equal("foo\r\nbar", clip.Text); // LF buffer -> CRLF clipboard
    }

    [Fact]
    public void Paste_over_a_selection_replaces_it_in_one_undo_step()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup("hello world");
        clip.Text = "there";
        Select(cmd, cur, 0, 6, 0, 11);
        cmd.Paste();
        Assert.Equal("hello there", buf.GetLine(0));

        Assert.True(cmd.Undo());
        Assert.Equal("hello world", buf.GetLine(0)); // one step restores the original selection
    }

    [Fact]
    public void Paste_converts_incoming_CRLF_to_the_buffer_ending()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup("");
        clip.Text = "a\r\nb\r\nc"; // CRLF from a Windows app
        cur.MoveTo(0, 0);
        cmd.Paste();
        Assert.Equal(3, buf.LineCount);
        Assert.Equal(LineEnding.Lf, buf.LineAt(0).Ending); // normalized to the buffer's LF
    }

    [Fact]
    public void Typing_over_a_selection_replaces_it()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, _) = Setup("hello world");
        Select(cmd, cur, 0, 0, 0, 5);
        cmd.InsertChar('H');
        Assert.Equal("H world", buf.GetLine(0));
        Assert.False(cmd.HasSelection);
    }

    [Fact]
    public void Ctrl_K_then_Ctrl_U_moves_a_line()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, _) = Setup("one", "two", "three");
        cur.MoveTo(0, 0);
        cmd.CutLine();
        Assert.Equal(2, buf.LineCount);
        Assert.Equal("two", buf.GetLine(0));

        cur.MoveTo(2, 0); // move to just past "three"'s line start... paste at end
        cur.MoveTo(1, buf.LineLength(1));
        cmd.Enter();      // make room on a fresh line
        cmd.PasteLine();
        Assert.Contains("one", buf.ToText());
    }

    [Fact]
    public void SelectAll_then_cut_empties_the_buffer()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup("alpha", "beta");
        cmd.SelectAll();
        Assert.True(cmd.HasSelection);
        cmd.Cut();
        Assert.Equal("alpha\r\nbeta", clip.Text);
        Assert.Equal(1, buf.LineCount);
        Assert.Equal("", buf.GetLine(0));
    }

    [Fact]
    public void Plain_move_collapses_the_selection()
    {
        (EditorCommands cmd, TextBuffer _, Cursor cur, _) = Setup("hello");
        Select(cmd, cur, 0, 0, 0, 3);
        Assert.True(cmd.HasSelection);
        cmd.Move(() => cur.Right(), extend: false);
        Assert.False(cmd.HasSelection);
    }
}
