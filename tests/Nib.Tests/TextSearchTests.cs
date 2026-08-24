using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The scan itself, with no editor around it. Wrapping and the whole-word boundary
/// are where a search quietly does the wrong thing: both directions have a start row
/// that has to be treated as two half-rows, and the boundary rule has to disagree
/// with <see cref="Cursor"/>'s whitespace-delimited words or "foo" stops matching
/// "foo," under the toggle.
/// </summary>
public class TextSearchTests
{
    private static TextBuffer Buffer(params string[] lines)
    {
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        return new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
    }

    private static SearchQuery Q(string text, bool matchCase = false, bool wholeWord = false)
        => new(text, matchCase, wholeWord);

    private static TextPosition At(int row, int col) => new(row, col);

    [Fact]
    public void Finds_the_next_match_after_the_caret()
    {
        TextBuffer buf = Buffer("alpha beta", "gamma beta");

        SearchMatch? hit = TextSearch.FindNext(buf, At(0, 0), Q("beta"), out bool wrapped);

        Assert.False(wrapped);
        Assert.Equal(At(0, 6), hit!.Value.Start);
        Assert.Equal(At(0, 10), hit.Value.End);
    }

    [Fact]
    public void Searching_from_the_end_of_a_hit_steps_to_the_next_one()
    {
        // This is what makes F3 walk: the editor leaves the caret on the far end of
        // the match it just found, and the next search starts there.
        TextBuffer buf = Buffer("alpha beta", "gamma beta");

        SearchMatch? hit = TextSearch.FindNext(buf, At(0, 10), Q("beta"), out bool wrapped);

        Assert.False(wrapped);
        Assert.Equal(At(1, 6), hit!.Value.Start);
    }

    [Fact]
    public void Wraps_to_the_top_and_says_so()
    {
        TextBuffer buf = Buffer("beta", "gamma", "delta");

        SearchMatch? hit = TextSearch.FindNext(buf, At(2, 0), Q("beta"), out bool wrapped);

        Assert.True(wrapped);
        Assert.Equal(At(0, 0), hit!.Value.Start);
    }

    [Fact]
    public void Wraps_to_the_bottom_searching_backwards()
    {
        TextBuffer buf = Buffer("alpha", "beta", "gamma");

        SearchMatch? hit = TextSearch.FindPrevious(buf, At(0, 0), Q("beta"), out bool wrapped);

        Assert.True(wrapped);
        Assert.Equal(At(1, 0), hit!.Value.Start);
    }

    [Fact]
    public void Finds_the_previous_match_before_the_caret_on_the_same_line()
    {
        // Three hits on one row: from the last one, backwards must land on the middle
        // one, not on the first and not on itself.
        TextBuffer buf = Buffer("xx yy xx yy xx");

        SearchMatch? hit = TextSearch.FindPrevious(buf, At(0, 12), Q("xx"), out bool wrapped);

        Assert.False(wrapped);
        Assert.Equal(At(0, 6), hit!.Value.Start);
    }

    [Fact]
    public void A_term_that_is_not_there_reports_nothing_and_does_not_claim_to_have_wrapped()
    {
        TextBuffer buf = Buffer("alpha", "gamma");

        Assert.Null(TextSearch.FindNext(buf, At(0, 0), Q("beta"), out bool forward));
        Assert.False(forward);

        Assert.Null(TextSearch.FindPrevious(buf, At(1, 5), Q("beta"), out bool backward));
        Assert.False(backward);
    }

    [Fact]
    public void An_empty_term_matches_nothing()
    {
        // Otherwise every position is a hit and the caret crawls forward one cell per
        // F3 forever.
        TextBuffer buf = Buffer("alpha");

        Assert.Null(TextSearch.FindNext(buf, At(0, 0), Q(""), out _));
        Assert.Empty(TextSearch.FindAll(buf, Q("")));
    }

    [Fact]
    public void Case_insensitive_by_default_and_exact_when_asked()
    {
        TextBuffer buf = Buffer("Alpha alpha");

        Assert.Equal(At(0, 0), TextSearch.FindNext(buf, At(0, 0), Q("alpha"), out _)!.Value.Start);
        Assert.Equal(At(0, 6), TextSearch.FindNext(buf, At(0, 0), Q("alpha", matchCase: true), out _)!.Value.Start);
    }

    [Theory]
    // The term is at index 6 in every line below; whether it counts is the boundary.
    [InlineData("xxxxx foo bar", true)]     // spaces either side
    [InlineData("xxxxx foo, bar", true)]    // punctuation is not a word character
    [InlineData("xxxxx foobar x", false)]   // runs into a letter
    [InlineData("xxxxx foo2 bar", false)]   // runs into a digit
    [InlineData("xxxxx foo_x bar", false)]  // underscore counts as a word character
    public void Whole_word_respects_letters_digits_and_underscore(string line, bool shouldMatch)
    {
        TextBuffer buf = Buffer(line);

        SearchMatch? hit = TextSearch.FindNext(buf, At(0, 6), Q("foo", wholeWord: true), out _);

        if (shouldMatch) Assert.Equal(At(0, 6), hit!.Value.Start);
        else Assert.Null(hit);
    }

    [Fact]
    public void Whole_word_matches_at_the_start_and_end_of_a_line()
    {
        // The ends of the line are boundaries too - an off-by-one here refuses the
        // first and last word in the file, which reads as "search is broken".
        TextBuffer buf = Buffer("foo bar foo");

        Assert.Equal(At(0, 0), TextSearch.FindNext(buf, At(0, 0), Q("foo", wholeWord: true), out _)!.Value.Start);
        Assert.Equal(At(0, 8), TextSearch.FindNext(buf, At(0, 3), Q("foo", wholeWord: true), out _)!.Value.Start);
    }

    [Fact]
    public void A_rejected_boundary_does_not_hide_a_real_match_behind_it()
    {
        // Scanning must resume one character past a rejected hit, not past its whole
        // length: "foofoo foo" has a rejected hit at 0 overlapping a rejected one at
        // 3, and the real one at 7 is only reachable if neither swallowed it.
        TextBuffer buf = Buffer("foofoo foo");

        SearchMatch? hit = TextSearch.FindNext(buf, At(0, 0), Q("foo", wholeWord: true), out _);

        Assert.Equal(At(0, 7), hit!.Value.Start);
    }

    [Fact]
    public void Overlapping_terms_are_found_once_each()
    {
        // "aaaa" contains "aa" at 0, 1 and 2; matches must not overlap or a replace
        // over them would corrupt its own output.
        TextBuffer buf = Buffer("aaaa");

        List<SearchMatch> all = TextSearch.FindAll(buf, Q("aa"));

        Assert.Equal(2, all.Count);
        Assert.Equal(At(0, 0), all[0].Start);
        Assert.Equal(At(0, 2), all[1].Start);
    }

    [Fact]
    public void Find_all_walks_the_whole_buffer_in_order()
    {
        TextBuffer buf = Buffer("beta one", "two", "three beta beta");

        List<SearchMatch> all = TextSearch.FindAll(buf, Q("beta"));

        Assert.Equal(3, all.Count);
        Assert.Equal(At(0, 0), all[0].Start);
        Assert.Equal(At(2, 6), all[1].Start);
        Assert.Equal(At(2, 11), all[2].Start);
    }

    [Fact]
    public void Find_all_can_start_part_way_through()
    {
        // What "replace all from here" is built on.
        TextBuffer buf = Buffer("beta", "beta", "beta");

        List<SearchMatch> all = TextSearch.FindAll(buf, Q("beta"), At(1, 0));

        Assert.Equal(2, all.Count);
        Assert.Equal(At(1, 0), all[0].Start);
    }

    [Fact]
    public void A_caret_outside_the_buffer_is_clamped_rather_than_throwing()
    {
        // A remembered position can outlive the rows it named: undo, a line join, or
        // a replace that shortened the file.
        TextBuffer buf = Buffer("alpha", "beta");

        Assert.NotNull(TextSearch.FindNext(buf, At(99, 99), Q("alpha"), out _));
        Assert.NotNull(TextSearch.FindPrevious(buf, At(-3, -3), Q("beta"), out _));
    }

    // ---- the `to` bound, which is what scopes a replace to a selection ----------

    [Fact]
    public void FindAll_stops_at_the_to_bound()
    {
        TextBuffer buf = Buffer("beta one", "beta two", "beta three");

        List<SearchMatch> hits = TextSearch.FindAll(buf, Q("beta"), from: null, to: At(1, 8));

        Assert.Equal(2, hits.Count);
        Assert.Equal(At(0, 0), hits[0].Start);
        Assert.Equal(At(1, 0), hits[1].Start);
    }

    [Fact]
    public void FindAll_excludes_a_match_straddling_the_to_bound()
    {
        // The bound falls in the middle of the second "beta". Replacing it would
        // rewrite two characters the user did not select, so it is outside the scope.
        TextBuffer buf = Buffer("beta and beta");

        List<SearchMatch> hits = TextSearch.FindAll(buf, Q("beta"), from: null, to: At(0, 11));

        Assert.Single(hits);
        Assert.Equal(At(0, 0), hits[0].Start);
    }

    [Fact]
    public void FindAll_honours_from_and_to_together()
    {
        TextBuffer buf = Buffer("beta one", "beta two", "beta three", "beta four");

        List<SearchMatch> hits = TextSearch.FindAll(buf, Q("beta"), At(1, 0), At(2, 10));

        Assert.Equal(2, hits.Count);
        Assert.Equal(At(1, 0), hits[0].Start);
        Assert.Equal(At(2, 0), hits[1].Start);
    }

    [Fact]
    public void FindAll_with_to_before_from_finds_nothing()
    {
        // An empty scope is not an error — it is a selection that contains no whole
        // match — and it must not be read as "unbounded".
        TextBuffer buf = Buffer("beta one", "beta two");

        Assert.Empty(TextSearch.FindAll(buf, Q("beta"), At(1, 0), At(0, 4)));
    }

    [Fact]
    public void FindAll_without_a_to_bound_still_reaches_the_end_of_the_buffer()
    {
        TextBuffer buf = Buffer("beta one", "beta two", "beta three");

        Assert.Equal(3, TextSearch.FindAll(buf, Q("beta")).Count);
    }
}
