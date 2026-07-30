using Nib.Highlight;
using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The two properties that make highlighting affordable on a large file.
///
/// <b>Convergence.</b> After an edit, re-tokenization walks forward until a line's
/// new carry state equals its cached one, then stops — so editing line 3 of a 20k
/// line file costs a couple of lines, not twenty thousand. Asserted on lines
/// tokenized rather than elapsed time, which would be flaky.
///
/// <b>Alignment.</b> The cache is indexed by row. If a line insert or delete leaves
/// it off by one, every row below the edit is coloured with another row's tokens —
/// a bug that looks like a rendering fault and isn't.
/// </summary>
public class HighlightCacheTests
{
    private const int Rows = 500;

    // A YAML document: cheap to tokenize, and the block-scalar test below needs a
    // grammar with real multi-line carry state.
    private static (TextBuffer Buffer, TextMateHighlighter Highlighter) Document(params string[] extra)
    {
        var lines = new List<Line>();
        foreach (string text in extra) lines.Add(new Line(text, LineEnding.Lf));
        for (int i = lines.Count; i < Rows; i++) lines.Add(new Line($"key{i}: value{i}", LineEnding.Lf));

        var buffer = new TextBuffer(lines, DocumentEncoding.Utf8NoBom, "settings.yaml");
        var highlighter = (TextMateHighlighter)TextMateHighlighter.Create(buffer);
        return (buffer, highlighter);
    }

    // Drive the walk to completion the way repeated frames would.
    private static void TokenizeAll(TextMateHighlighter h)
    {
        for (int i = 0; i < Rows; i++) h.TokenizeWindow(0, Rows);
    }

    [Fact]
    public void A_file_type_with_no_grammar_gets_the_null_highlighter()
    {
        var buffer = new TextBuffer([new Line("hello", LineEnding.None)], DocumentEncoding.Utf8NoBom, "notes.xyz");
        Assert.IsType<NullHighlighter>(TextMateHighlighter.Create(buffer));
    }

    [Fact]
    public void Rows_are_uncoloured_until_they_are_tokenized()
    {
        (_, TextMateHighlighter h) = Document();
        Assert.Null(h.Spans(Rows - 1)); // first paint colours nothing it hasn't reached
    }

    [Fact]
    public void Tokenized_rows_have_spans()
    {
        (_, TextMateHighlighter h) = Document();
        h.TokenizeWindow(0, 40);
        Assert.NotEmpty(h.Spans(0)!);
    }

    [Fact]
    public void An_edit_stops_re_tokenizing_as_soon_as_the_carry_state_converges()
    {
        (TextBuffer buffer, TextMateHighlighter h) = Document();
        TokenizeAll(h);

        int before = h.LinesTokenized;
        buffer.InsertText(3, 0, "x");         // raises LinesChanged, dirtying row 3
        h.TokenizeWindow(0, 40);

        // Row 3 is re-tokenized; row 4 confirms convergence. Anything beyond a
        // handful means the walk is not stopping and the file is being redone.
        Assert.InRange(h.LinesTokenized - before, 1, 3);
    }

    [Fact]
    public void An_edit_that_changes_the_carry_state_keeps_walking()
    {
        // Opening a triple-quoted string swallows everything below it, so the carry
        // state genuinely changes and convergence must NOT trigger.
        var lines = new List<Line>();
        for (int i = 0; i < Rows; i++) lines.Add(new Line($"key{i} = 'value{i}'", LineEnding.Lf));
        var buffer = new TextBuffer(lines, DocumentEncoding.Utf8NoBom, "settings.py");
        var h = (TextMateHighlighter)TextMateHighlighter.Create(buffer);
        for (int i = 0; i < Rows; i++) h.TokenizeWindow(0, Rows);

        int before = h.LinesTokenized;
        buffer.InsertText(3, 0, "\"\"\"");
        h.TokenizeWindow(0, 40);

        Assert.True(h.LinesTokenized - before > 3,
            $"expected the walk to continue past the edit, tokenized {h.LinesTokenized - before}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(Rows - 1)]
    public void Splitting_a_line_keeps_the_cache_aligned(int row)
    {
        (TextBuffer buffer, TextMateHighlighter h) = Document();
        TokenizeAll(h);

        buffer.SplitLine(row, 0);

        Assert.Equal(buffer.LineCount, h.CachedRows);
    }

    [Fact]
    public void A_multi_line_paste_and_its_undo_keep_the_cache_aligned()
    {
        (TextBuffer buffer, TextMateHighlighter h) = Document();
        TokenizeAll(h);

        TextPosition end = buffer.InsertMultiline(new TextPosition(2, 0), "a: 1\nb: 2\nc: 3\n");
        Assert.Equal(buffer.LineCount, h.CachedRows);

        buffer.DeleteRange(new TextPosition(2, 0), end);
        Assert.Equal(buffer.LineCount, h.CachedRows);
    }

    [Fact]
    public void Deleting_a_range_across_lines_keeps_the_cache_aligned()
    {
        (TextBuffer buffer, TextMateHighlighter h) = Document();
        TokenizeAll(h);

        buffer.DeleteRange(new TextPosition(10, 0), new TextPosition(20, 0));

        Assert.Equal(buffer.LineCount, h.CachedRows);
    }

    [Fact]
    public void Rows_below_an_insert_keep_the_spans_that_belong_to_them()
    {
        (TextBuffer buffer, TextMateHighlighter h) = Document();
        TokenizeAll(h);
        IReadOnlyList<StyledSpan> before = h.Spans(100)!;

        buffer.SplitLine(3, 0);      // everything below shifts down one row
        h.TokenizeWindow(0, 40);

        Assert.Equal(before, h.Spans(101));
    }

    [Fact]
    public void Switching_theme_recolours_without_losing_alignment()
    {
        (TextBuffer buffer, TextMateHighlighter h) = Document();
        TokenizeAll(h);
        IReadOnlyList<StyledSpan> darkPlus = h.Spans(0)!;

        h.CycleTheme();
        TokenizeAll(h);

        Assert.Equal(buffer.LineCount, h.CachedRows);
        Assert.NotEqual(darkPlus, h.Spans(0));
    }

    /// <summary>
    /// The per-line timeout is what stops Oniguruma backtracking forever on a
    /// minified line. The line must come back — coloured or not — not hang.
    /// </summary>
    [Fact]
    public void A_pathological_line_returns_rather_than_hanging()
    {
        var buffer = new TextBuffer(
            [new Line(string.Concat(Enumerable.Repeat("function a(){return {b:[1,2,3]};}", 4000)), LineEnding.None)],
            DocumentEncoding.Utf8NoBom, "bundle.min.js");
        var h = (TextMateHighlighter)TextMateHighlighter.Create(buffer);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        h.TokenizeWindow(0, 1);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 3000, $"took {clock.ElapsedMilliseconds} ms");
        Assert.NotNull(h.Spans(0));
    }
}
