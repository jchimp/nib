using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// What ^W reports. The counts are easy; the two rules underneath them are not, and
/// both are the kind that make a number quietly wrong rather than obviously wrong:
/// which trailing line counts, and whether a CRLF is one character or two.
/// </summary>
public class TextStatsTests
{
    private static TextBuffer Buffer(params string[] lines)
    {
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        return new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
    }

    [Fact]
    public void Counts_lines_words_and_characters_of_a_buffer()
    {
        TextBuffer buf = Buffer("one two", "three");

        TextStats stats = TextStats.Of(buf);

        Assert.Equal(2, stats.Lines);
        Assert.Equal(3, stats.Words);
        Assert.Equal(13, stats.Chars); // 7 + the LF + 5
    }

    [Fact]
    public void A_file_that_ended_with_a_newline_does_not_count_the_empty_line_after_it()
    {
        // The same rule the "Wrote N lines" message uses. The two sharing it is the
        // point: ^W and ^S disagreeing by one about the same buffer would make both
        // numbers untrustworthy.
        TextBuffer buf = Buffer("one", "two", "");

        Assert.Equal(2, TextStats.Of(buf).Lines);
        Assert.Equal(2, TextStats.LineCount(buf));
    }

    [Fact]
    public void A_buffer_holding_one_empty_line_still_counts_as_one_line()
    {
        // The trailing-line rule must not subtract the only line there is, or a new
        // empty file reports zero lines and then one the moment anything is typed.
        TextBuffer buf = Buffer("");

        Assert.Equal(1, TextStats.LineCount(buf));
        Assert.Equal(0, TextStats.Of(buf).Words);
        Assert.Equal(0, TextStats.Of(buf).Chars);
    }

    [Fact]
    public void A_CRLF_line_costs_two_characters()
    {
        // Terminators are characters the file holds, and a Windows file holds two of
        // them per line. Counting one would under-report every config on this machine.
        var crlf = new TextBuffer(
            [new Line("one", LineEnding.CrLf), new Line("two", LineEnding.None)],
            DocumentEncoding.Utf8NoBom, null);

        Assert.Equal(8, TextStats.Of(crlf).Chars); // 3 + CR + LF + 3
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData("  one  ", 1)]
    [InlineData("one two three", 3)]
    [InlineData("one    two", 2)]        // a run of spaces is one separator
    [InlineData("one\ttwo", 2)]          // and so is a tab
    [InlineData("foo,bar", 1)]           // whitespace-delimited, like wc -w
    [InlineData("a.b.c", 1)]
    public void Words_are_runs_of_non_whitespace(string line, int expected)
    {
        Assert.Equal(expected, TextStats.Of(Buffer(line)).Words);
    }

    [Fact]
    public void Counts_a_span_of_text_as_a_selection_would_hand_it_over()
    {
        TextStats stats = TextStats.Of("one two\nthree");

        Assert.Equal(2, stats.Lines);
        Assert.Equal(3, stats.Words);
        Assert.Equal(13, stats.Chars);
    }

    [Fact]
    public void A_span_ending_in_a_newline_does_not_count_an_extra_line()
    {
        // Selecting a whole line including its terminator is one line selected, not
        // one and an empty one.
        Assert.Equal(1, TextStats.Of("one\n").Lines);
        Assert.Equal(2, TextStats.Of("one\ntwo\n").Lines);
    }

    [Fact]
    public void A_span_split_by_CRLF_is_two_lines_not_three()
    {
        TextStats stats = TextStats.Of("one\r\ntwo");

        Assert.Equal(2, stats.Lines);
        Assert.Equal(8, stats.Chars);
    }

    [Fact]
    public void An_empty_span_is_all_zeroes()
    {
        Assert.Equal(new TextStats(0, 0, 0), TextStats.Of(""));
    }
}
