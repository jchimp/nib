using Nib.Model;
using Xunit;

namespace Nib.Tests;

public class TextBufferTests
{
    private static TextBuffer Buffer(params string[] lines)
    {
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        return new TextBuffer(list, DocumentEncoding.Utf8NoBom, path: null);
    }

    [Fact]
    public void InsertChar_into_middle_of_line()
    {
        var b = Buffer("helo");
        b.InsertChar(0, 2, 'l');
        Assert.Equal("hello", b.GetLine(0));
        Assert.True(b.IsModified);
    }

    [Fact]
    public void New_buffer_is_not_modified()
        => Assert.False(Buffer("x").IsModified);

    [Fact]
    public void SplitLine_moves_the_tail_and_keeps_the_original_ending()
    {
        var b = Buffer("hello world");
        b.SplitLine(0, 5);
        Assert.Equal(2, b.LineCount);
        Assert.Equal("hello", b.GetLine(0));
        Assert.Equal(" world", b.GetLine(1));
        // The tail keeps the original terminator (None here); the head takes the default (LF).
        Assert.Equal(LineEnding.None, b.LineAt(1).Ending);
        Assert.Equal(LineEnding.Lf, b.LineAt(0).Ending);
    }

    [Fact]
    public void DeleteBackward_at_column_zero_joins_previous_line()
    {
        var b = Buffer("foo", "bar");
        b.DeleteBackward(1, 0);
        Assert.Equal(1, b.LineCount);
        Assert.Equal("foobar", b.GetLine(0));
    }

    [Fact]
    public void DeleteForward_at_end_of_line_joins_next_line()
    {
        var b = Buffer("foo", "bar");
        b.DeleteForward(0, 3);
        Assert.Equal(1, b.LineCount);
        Assert.Equal("foobar", b.GetLine(0));
    }

    [Fact]
    public void DeleteBackward_at_origin_is_a_noop()
    {
        var b = Buffer("abc");
        b.DeleteBackward(0, 0);
        Assert.Equal("abc", b.GetLine(0));
        Assert.False(b.IsModified);
    }

    [Fact]
    public void Split_then_join_restores_the_text()
    {
        var b = Buffer("hello world");
        b.SplitLine(0, 5);
        b.DeleteBackward(1, 0);
        Assert.Equal(1, b.LineCount);
        Assert.Equal("hello world", b.GetLine(0));
    }

    [Fact]
    public void Dominant_ending_drives_new_line_breaks()
    {
        var list = new List<Line>
        {
            new("a", LineEnding.CrLf),
            new("b", LineEnding.CrLf),
            new("c", LineEnding.None),
        };
        var b = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
        b.SplitLine(0, 1); // break after "a"
        Assert.Equal(LineEnding.CrLf, b.LineAt(0).Ending);
    }
}
