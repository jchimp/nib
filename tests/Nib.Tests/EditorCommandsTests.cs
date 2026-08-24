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
    public void Copy_collapses_the_selection_and_leaves_the_caret_where_it_was()
    {
        (EditorCommands cmd, _, Cursor cur, FakeClipboard clip) = Setup("hello world");
        Select(cmd, cur, 0, 6, 0, 11);
        cmd.Copy();

        Assert.Equal("world", clip.Text);
        Assert.False(cmd.HasSelection);
        Assert.Equal(0, cur.Row);
        Assert.Equal(11, cur.Col);
    }

    [Fact]
    public void A_shift_move_after_a_copy_selects_afresh_rather_than_extending()
    {
        (EditorCommands cmd, _, Cursor cur, FakeClipboard clip) = Setup("hello world");
        Select(cmd, cur, 0, 0, 0, 5);
        cmd.Copy();

        // The dropped anchor is the point: extending from column 5, not from 0.
        cmd.Move(cur.Right, extend: true);
        cmd.Copy();
        Assert.Equal(" ", clip.Text);
    }

    [Fact]
    public void Copy_with_no_selection_leaves_the_clipboard_alone()
    {
        (EditorCommands cmd, _, _, FakeClipboard clip) = Setup("hello world");
        clip.Text = "earlier";
        cmd.Copy();
        Assert.Equal("earlier", clip.Text);
    }

    [Fact]
    public void Go_to_line_moves_to_column_zero_and_drops_the_selection()
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup("one", "two", "three");
        Select(cmd, cur, 0, 0, 0, 3);

        Assert.True(cmd.TryGoToLine(3));
        Assert.Equal(2, cur.Row);
        Assert.Equal(0, cur.Col);
        Assert.False(cmd.HasSelection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(4)]
    public void Go_to_a_line_outside_the_buffer_refuses_and_does_not_move(int line)
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup("one", "two", "three");
        cur.MoveTo(1, 2);

        Assert.False(cmd.TryGoToLine(line));
        Assert.Equal(1, cur.Row);
        Assert.Equal(2, cur.Col);
    }

    [Fact]
    public void Clear_selection_leaves_the_caret_alone()
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup("hello world");
        Select(cmd, cur, 0, 0, 0, 5);
        cmd.ClearSelection();

        Assert.False(cmd.HasSelection);
        Assert.Equal(5, cur.Col);
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

    // ---- stale anchors ------------------------------------------------------
    // Selection stores a raw anchor position and the buffer can shrink underneath
    // it. These cover the sequence that used to crash the editor: a selection
    // gesture the user backed out of leaves an anchor that IsActive calls inactive,
    // so Backspace took the no-selection path and left it behind; joining two lines
    // then put it past the end of the buffer, and the next Copy indexed off the end.

    [Fact]
    public void An_edit_drops_a_collapsed_anchor()
    {
        (EditorCommands cmd, TextBuffer _, Cursor cur, _) = Setup("alpha", "beta");

        // Shift+Right then Shift+Left: anchored, but start == end so nothing counts
        // as selected.
        cmd.Move(cur.Right, extend: true);
        cmd.Move(cur.Left, extend: true);
        Assert.True(cmd.Selection.HasAnchor);
        Assert.False(cmd.HasSelection);

        cmd.InsertChar('x');

        Assert.False(cmd.Selection.HasAnchor);
    }

    [Fact]
    public void Copying_after_a_line_join_that_followed_a_collapsed_selection_does_not_throw()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup("alpha", "beta", "gamma");

        cmd.Move(cur.DocumentEnd, extend: false);
        cmd.Move(cur.Home, extend: false);      // start of the last line
        cmd.Move(cur.Right, extend: true);      // anchor here...
        cmd.Move(cur.Left, extend: true);       // ...and collapse back onto it

        cmd.Backspace();                        // joins the last line onto the one above
        Assert.Equal(2, buf.LineCount);
        Assert.Equal("betagamma", buf.GetLine(1));

        cmd.Move(cur.Right, extend: true);      // re-extend — must anchor afresh
        cmd.Copy();

        // The caret sits at the join, so one Shift+Right takes the "g" of "gamma".
        // Before the fix this threw: the anchor still named row 2, which was gone.
        Assert.Equal("g", clip.Text);
    }

    /// <summary>
    /// Belt and braces for the same class of bug: even handed an anchor pointing past
    /// the end of the buffer, the selection must clamp rather than index off the end.
    /// </summary>
    [Fact]
    public void A_selection_anchored_past_the_end_of_the_buffer_clamps()
    {
        (EditorCommands cmd, TextBuffer buf, Cursor cur, FakeClipboard clip) = Setup("alpha", "beta", "gamma");

        Select(cmd, cur, 2, 0, 2, 5);
        buf.DeleteRange(new TextPosition(0, 5), new TextPosition(2, 0)); // collapse to one line
        cur.MoveTo(0, 0);

        cmd.Copy();  // must not throw
        cmd.Cut();   // nor this

        Assert.NotNull(clip.Text);
    }

    // ---- counts (^W) --------------------------------------------------------

    [Fact]
    public void Counting_a_selection_counts_only_what_is_selected()
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup("one two", "three four", "five");

        Select(cmd, cur, 0, 4, 1, 5);  // "two\nthree"

        TextStats stats = cmd.CountSelection();
        Assert.Equal(2, stats.Lines);
        Assert.Equal(2, stats.Words);
        Assert.Equal(9, stats.Chars);
    }

    [Fact]
    public void Counting_with_no_selection_returns_zeroes_rather_than_the_whole_buffer()
    {
        // The caller checks HasSelection and asks the buffer instead. Returning the
        // file's totals here would make "Selected: 3 lines" appear over a file with
        // nothing highlighted.
        (EditorCommands cmd, _, _, _) = Setup("one two", "three");

        Assert.False(cmd.HasSelection);
        Assert.Equal(default, cmd.CountSelection());
    }

    [Fact]
    public void Counting_a_selection_left_stale_by_an_edit_clamps_rather_than_throwing()
    {
        // Same guard every other consumer of the selection gets: the count goes
        // through SelectionRange, so an anchor naming a row the buffer no longer has
        // is a wrong number and not a crash.
        (EditorCommands cmd, TextBuffer buf, Cursor cur, _) = Setup("alpha", "beta", "gamma");

        Select(cmd, cur, 2, 0, 2, 5);
        buf.DeleteRange(new TextPosition(0, 5), new TextPosition(2, 0));
        cur.MoveTo(0, 0);

        cmd.CountSelection(); // must not throw
    }

    // ---- search and replace -------------------------------------------------

    private static SearchQuery Q(string text, bool matchCase = false, bool wholeWord = false)
        => new(text, matchCase, wholeWord);

    [Fact]
    public void A_found_match_is_selected_so_the_view_paints_it()
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup("alpha beta", "gamma");

        Assert.Equal(SearchOutcome.Found, cmd.Find(Q("beta"), backwards: false));

        Assert.True(cmd.HasSelection);
        Assert.Equal(new TextPosition(0, 6), cmd.Selection.Anchor);
        Assert.Equal(0, cur.Row);
        Assert.Equal(10, cur.Col); // caret on the far end, which is where F3 resumes
    }

    [Fact]
    public void Find_again_walks_forward_one_match_at_a_time()
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup("beta", "beta", "beta");

        cmd.Find(Q("beta"), backwards: false);
        Assert.Equal(0, cur.Row);

        Assert.Equal(SearchOutcome.Found, cmd.FindAgain(backwards: false));
        Assert.Equal(1, cur.Row);

        Assert.Equal(SearchOutcome.Found, cmd.FindAgain(backwards: false));
        Assert.Equal(2, cur.Row);

        // Off the bottom and round to the top, which is a different outcome so the
        // message row can say it happened.
        Assert.Equal(SearchOutcome.FoundWrapped, cmd.FindAgain(backwards: false));
        Assert.Equal(0, cur.Row);
    }

    [Fact]
    public void Searching_backwards_moves_off_the_match_it_is_standing_on()
    {
        // The caret sits past the current hit, so a naive backwards search finds that
        // same hit again and Shift+F3 never moves. It has to start from the near end.
        (EditorCommands cmd, _, Cursor cur, _) = Setup("beta", "beta", "beta");

        cmd.Find(Q("beta"), backwards: false);
        Assert.Equal(0, cur.Row);

        Assert.Equal(SearchOutcome.FoundWrapped, cmd.FindAgain(backwards: true));
        Assert.Equal(2, cur.Row);

        Assert.Equal(SearchOutcome.Found, cmd.FindAgain(backwards: true));
        Assert.Equal(1, cur.Row);
    }

    /// <summary>
    /// ROADMAP acceptance: search reports "not found" without losing the caret.
    /// Jumping somewhere plausible instead is worse than not moving at all, because
    /// the user's place in the file is gone and nothing says so.
    /// </summary>
    [Fact]
    public void A_term_that_is_not_there_leaves_the_caret_exactly_where_it_was()
    {
        (EditorCommands cmd, _, Cursor cur, _) = Setup("alpha", "gamma", "delta");
        cur.MoveTo(1, 3);

        Assert.Equal(SearchOutcome.NotFound, cmd.Find(Q("beta"), backwards: false));

        Assert.Equal(1, cur.Row);
        Assert.Equal(3, cur.Col);
        Assert.False(cmd.HasSelection);
    }

    [Fact]
    public void Find_again_with_nothing_to_find_again_says_so()
    {
        // F3 before any ^F. A null check in Program.cs would be untestable; an
        // outcome is not.
        (EditorCommands cmd, _, _, _) = Setup("alpha");

        Assert.Equal(SearchOutcome.NoQuery, cmd.FindAgain(backwards: false));
        Assert.Equal(SearchOutcome.NoQuery, cmd.Find(Q(""), backwards: false));
    }

    [Fact]
    public void The_term_and_its_toggles_survive_for_the_next_search()
    {
        (EditorCommands cmd, _, _, _) = Setup("Alpha alpha");

        cmd.Find(Q("alpha", matchCase: true, wholeWord: true), backwards: false);

        Assert.Equal("alpha", cmd.Search.Text);
        Assert.True(cmd.Search.MatchCase);
        Assert.True(cmd.Search.WholeWord);
    }

    [Fact]
    public void Replacing_one_match_leaves_the_caret_past_what_it_inserted()
    {
        // Resuming from the match *start* would find the replacement inside itself,
        // and "a" -> "aa" would never stop.
        (EditorCommands cmd, TextBuffer buf, Cursor cur, _) = Setup("a b a");

        cmd.Find(Q("a"), backwards: false);
        cmd.ReplaceMatch(new SearchMatch(new TextPosition(0, 0), new TextPosition(0, 1)), "aa");

        Assert.Equal("aa b a", buf.GetLine(0));
        Assert.Equal(2, cur.Col);
    }

    [Fact]
    public void Replace_all_rewrites_every_match_and_counts_them()
    {
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup("beta one", "two", "three beta beta");

        Assert.Equal(3, cmd.ReplaceAll(Q("beta"), "GAMMA"));

        Assert.Equal("GAMMA one", buf.GetLine(0));
        Assert.Equal("two", buf.GetLine(1));
        Assert.Equal("three GAMMA GAMMA", buf.GetLine(2));
    }

    /// <summary>
    /// ROADMAP acceptance: replace-all is a single undo step. The whole run is one
    /// Edit spanning the first hit to the last, so this must come back byte-exact
    /// after exactly one Undo - not after three.
    /// </summary>
    [Fact]
    public void Replace_all_undoes_byte_exactly_in_one_step()
    {
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup("beta one", "two", "three beta beta");
        string before = buf.ToText();

        cmd.ReplaceAll(Q("beta"), "GAMMA");
        Assert.NotEqual(before, buf.ToText());

        Assert.True(cmd.Undo());
        Assert.Equal(before, buf.ToText());
        Assert.False(cmd.Undo()); // and there was only ever the one step to take
    }

    [Fact]
    public void Replace_all_terminates_when_the_replacement_contains_the_term()
    {
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup("a a a");

        Assert.Equal(3, cmd.ReplaceAll(Q("a"), "aa"));
        Assert.Equal("aa aa aa", buf.GetLine(0));
    }

    [Fact]
    public void Replace_all_survives_a_replacement_that_spans_lines()
    {
        // The span rewrite goes back through InsertMultiline, so a newline in the
        // replacement has to split lines properly rather than land as a literal.
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup("one X two", "three X four");
        string before = buf.ToText();

        Assert.Equal(2, cmd.ReplaceAll(Q("X"), "\nY\n"));

        Assert.Equal(6, buf.LineCount);
        Assert.True(cmd.Undo());
        Assert.Equal(before, buf.ToText());
    }

    [Fact]
    public void Replace_all_can_start_part_way_down_the_file()
    {
        // What the "A" answer in confirm-each does with the rest of the file.
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup("beta", "beta", "beta");

        Assert.Equal(2, cmd.ReplaceAll(Q("beta"), "GAMMA", new TextPosition(1, 0)));

        Assert.Equal("beta", buf.GetLine(0));
        Assert.Equal("GAMMA", buf.GetLine(1));
        Assert.Equal("GAMMA", buf.GetLine(2));
    }

    [Fact]
    public void Replace_all_with_no_matches_changes_nothing()
    {
        (EditorCommands cmd, TextBuffer buf, _, _) = Setup("alpha", "gamma");
        string before = buf.ToText();

        Assert.Equal(0, cmd.ReplaceAll(Q("beta"), "X"));

        Assert.Equal(before, buf.ToText());
        Assert.False(cmd.Undo()); // and did not record an empty step to undo
    }

    [Fact]
    public void Skipping_a_match_does_not_offer_it_again()
    {
        // The skip path in confirm-each: the caret is already past the hit, so
        // dropping the highlight is all it takes for the next search to move on.
        (EditorCommands cmd, _, Cursor cur, _) = Setup("beta beta");

        cmd.Find(Q("beta"), backwards: false);
        Assert.Equal(new TextPosition(0, 0), cmd.Selection.Anchor);
        Assert.Equal(4, cur.Col);
        cmd.ClearSelection();

        Assert.Equal(SearchOutcome.Found, cmd.FindAgain(backwards: false));
        Assert.Equal(new TextPosition(0, 5), cmd.Selection.Anchor);
        Assert.Equal(9, cur.Col);
    }
}
