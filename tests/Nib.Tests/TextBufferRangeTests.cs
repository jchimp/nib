using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The ranged verbs undo and paste are built on. The load-bearing property is that
/// <c>InsertMultiline(start, GetRange(start, end))</c> after <c>DeleteRange</c> is a
/// byte-for-byte identity, including mixed line endings.
/// </summary>
public class TextBufferRangeTests
{
    private static TextBuffer Buffer(params (string Text, LineEnding Ending)[] lines)
    {
        var list = new List<Line>();
        foreach ((string text, LineEnding ending) in lines)
            list.Add(new Line(text, ending));
        return new TextBuffer(list, DocumentEncoding.Utf8NoBom, path: null);
    }

    [Fact]
    public void GetRange_within_a_line_is_a_substring()
    {
        var b = Buffer(("hello world", LineEnding.None));
        Assert.Equal("lo w", b.GetRange(new(0, 3), new(0, 7)));
    }

    [Fact]
    public void GetRange_across_lines_includes_the_crossed_terminators()
    {
        var b = Buffer(("foo", LineEnding.Lf), ("bar", LineEnding.CrLf), ("baz", LineEnding.None));
        Assert.Equal("oo\nbar\r\nba", b.GetRange(new(0, 1), new(2, 2)));
    }

    [Fact]
    public void DeleteRange_within_a_line_removes_the_span()
    {
        var b = Buffer(("hello world", LineEnding.None));
        b.DeleteRange(new(0, 5), new(0, 11));
        Assert.Equal("hello", b.GetLine(0));
    }

    [Fact]
    public void DeleteRange_across_lines_merges_and_keeps_the_lower_ending()
    {
        var b = Buffer(("foo", LineEnding.Lf), ("bar", LineEnding.CrLf), ("baz", LineEnding.None));
        b.DeleteRange(new(0, 1), new(2, 2));
        Assert.Equal(1, b.LineCount);
        Assert.Equal("fz", b.GetLine(0));
        Assert.Equal(LineEnding.None, b.LineAt(0).Ending); // baz's ending survives
    }

    [Fact]
    public void InsertMultiline_preserves_embedded_endings()
    {
        var b = Buffer(("fz", LineEnding.None));
        TextPosition end = b.InsertMultiline(new(0, 1), "oo\nbar\r\nba");
        Assert.Equal(3, b.LineCount);
        Assert.Equal(("foo", LineEnding.Lf), (b.GetLine(0), b.LineAt(0).Ending));
        Assert.Equal(("bar", LineEnding.CrLf), (b.GetLine(1), b.LineAt(1).Ending));
        Assert.Equal(("baz", LineEnding.None), (b.GetLine(2), b.LineAt(2).Ending));
        Assert.Equal(new TextPosition(2, 2), end); // just past the inserted "ba"
    }

    [Fact]
    public void InsertMultiline_without_a_newline_is_an_inline_insert()
    {
        var b = Buffer(("held", LineEnding.None));
        TextPosition end = b.InsertMultiline(new(0, 2), "ave-wor");
        Assert.Equal(1, b.LineCount);
        Assert.Equal("heave-world", b.GetLine(0));
        Assert.Equal(new TextPosition(0, 9), end);
    }

    [Fact]
    public void Delete_then_reinsert_the_range_is_byte_for_byte_identity()
    {
        var b = Buffer(("foo", LineEnding.Lf), ("bar", LineEnding.CrLf), ("baz", LineEnding.None));
        string before = b.ToText();

        var start = new TextPosition(0, 1);
        var end = new TextPosition(2, 2);
        string cut = b.GetRange(start, end);
        b.DeleteRange(start, end);
        b.InsertMultiline(start, cut);

        Assert.Equal(before, b.ToText());
    }

    [Fact]
    public void Multi_line_paste_of_500_lines_lands_the_expected_shape()
    {
        var b = Buffer(("", LineEnding.None));
        var block = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line{i}"));
        b.InsertMultiline(new(0, 0), block);

        Assert.Equal(500, b.LineCount);
        Assert.Equal("line0", b.GetLine(0));
        Assert.Equal("line499", b.GetLine(499));
        // The pasted breaks are LF; the final line keeps the original's None ending.
        Assert.Equal(LineEnding.Lf, b.LineAt(0).Ending);
        Assert.Equal(LineEnding.None, b.LineAt(499).Ending);
    }
}
