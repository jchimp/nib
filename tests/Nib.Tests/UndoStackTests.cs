using Nib.Model;
using Nib.Model.Undo;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The phase's most important tests, mirroring the byte-for-byte file round-trip:
/// any sequence of edits must undo to the exact original text and redo to the exact
/// edited text. A tiny applier stands in for <c>EditorCommands</c> — it mutates the
/// buffer and records an <see cref="Edit"/> the same way, so the coalescence and
/// caret behaviour under test is the real behaviour.
/// </summary>
public class UndoStackTests
{
    private sealed class Harness
    {
        public readonly TextBuffer Buffer;
        public readonly Cursor Cursor;
        public readonly UndoStack Stack;
        public long Clock;

        public Harness(params string[] lines)
        {
            var list = new List<Line>();
            for (int i = 0; i < lines.Length; i++)
                list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
            Buffer = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
            Cursor = new Cursor(Buffer);
            Stack = new UndoStack(() => Clock);
        }

        private TextPosition Pos => new(Cursor.Row, Cursor.Col);

        public void Type(string text, bool coalesce = true)
        {
            TextPosition before = Pos;
            TextPosition end = Buffer.InsertMultiline(before, text);
            Cursor.MoveTo(end.Row, end.Col);
            Stack.Record(new Edit(before, "", text, before, Pos, Clock, coalesce && Single(text)));
        }

        public void Backspace()
        {
            if (Cursor.Col == 0 && Cursor.Row == 0) return;
            TextPosition before = Pos;
            TextPosition start = Cursor.Col > 0
                ? new(Cursor.Row, Cursor.Col - 1)
                : new(Cursor.Row - 1, Buffer.LineLength(Cursor.Row - 1));
            string removed = Buffer.GetRange(start, before);
            Buffer.DeleteRange(start, before);
            Cursor.MoveTo(start.Row, start.Col);
            Stack.Record(new Edit(start, removed, "", before, Pos, Clock, Single(removed)));
        }

        public void Paste(string text)
        {
            TextPosition before = Pos;
            TextPosition end = Buffer.InsertMultiline(before, text);
            Cursor.MoveTo(end.Row, end.Col);
            // Paste never coalesces — it is always its own undo step.
            Stack.Record(new Edit(before, "", text, before, Pos, Clock, Coalesce: false));
        }

        public bool Undo() => Stack.Undo(Buffer, Cursor);
        public bool Redo() => Stack.Redo(Buffer, Cursor);
        public string Text => Buffer.ToText();

        private static bool Single(string s) => s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0;
    }

    [Fact]
    public void Typing_a_word_then_one_undo_removes_the_whole_word()
    {
        var h = new Harness("");
        foreach (char c in "hello") { h.Type(c.ToString()); h.Clock += 10; } // all within 300 ms

        Assert.True(h.Undo());
        Assert.Equal("", h.Text);
        Assert.False(h.Stack.CanUndo); // one coalesced step, now empty
    }

    [Fact]
    public void A_pause_breaks_the_typing_run_into_separate_undo_steps()
    {
        var h = new Harness("");
        foreach (char c in "abc") { h.Type(c.ToString()); h.Clock += 10; }
        h.Clock += TimeSpan.FromMilliseconds(400).Ticks; // long pause
        foreach (char c in "def") { h.Type(c.ToString()); h.Clock += 10; }

        Assert.True(h.Undo());
        Assert.Equal("abc", h.Text); // second run undone
        Assert.True(h.Undo());
        Assert.Equal("", h.Text);    // first run undone
    }

    [Fact]
    public void Contiguous_backspaces_coalesce_into_one_step()
    {
        var h = new Harness("hello");
        h.Cursor.MoveTo(0, 5);
        for (int i = 0; i < 3; i++) { h.Backspace(); h.Clock += 10; }
        Assert.Equal("he", h.Text);

        Assert.True(h.Undo());
        Assert.Equal("hello", h.Text); // three backspaces, one undo
    }

    [Fact]
    public void Redo_after_undo_reproduces_the_edited_text()
    {
        var h = new Harness("");
        foreach (char c in "hello") { h.Type(c.ToString()); h.Clock += 10; }
        h.Undo();
        Assert.True(h.Redo());
        Assert.Equal("hello", h.Text);
    }

    [Fact]
    public void A_new_edit_after_undo_discards_the_redo_branch()
    {
        var h = new Harness("");
        foreach (char c in "abc") { h.Type(c.ToString()); h.Clock += 10; }
        h.Undo();
        h.Clock += TimeSpan.FromMilliseconds(400).Ticks;
        h.Type("x");
        Assert.False(h.Stack.CanRedo);
    }

    [Fact]
    public void Any_sequence_undoes_to_the_exact_original_and_redoes_to_the_exact_edited()
    {
        var h = new Harness("one", "two", "three");
        string original = h.Text;

        // A deliberately mixed session: typing, a multi-line paste, more typing,
        // some deletes — with pauses so it forms several undo groups.
        h.Cursor.MoveTo(0, 3);
        h.Type("!");
        h.Clock += TimeSpan.FromMilliseconds(400).Ticks;
        h.Cursor.MoveTo(1, 0);
        h.Paste("alpha\nbeta\n");
        h.Clock += TimeSpan.FromMilliseconds(400).Ticks;
        h.Cursor.MoveTo(0, 0);
        foreach (char c in "PRE") { h.Type(c.ToString()); h.Clock += 10; }
        h.Clock += TimeSpan.FromMilliseconds(400).Ticks;
        h.Cursor.MoveTo(0, 6);
        for (int i = 0; i < 2; i++) { h.Backspace(); h.Clock += 10; }

        string edited = h.Text;
        Assert.NotEqual(original, edited);

        while (h.Undo()) { }
        Assert.Equal(original, h.Text);

        while (h.Redo()) { }
        Assert.Equal(edited, h.Text);
    }
}
