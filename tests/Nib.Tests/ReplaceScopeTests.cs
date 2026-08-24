using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The bound a replace-in-selection pass runs inside. The arithmetic here is the part
/// of that feature with edge cases — the editor loop around it cannot be tested at all
/// — and the one that goes wrong quietly: a scope whose end fails to follow the text
/// walks the last match of a selection out of bounds, and the user sees a replace that
/// stopped one hit early rather than an error.
/// </summary>
public class ReplaceScopeTests
{
    private static TextPosition At(int row, int col) => new(row, col);

    private static SearchMatch Match(int row, int start, int end)
        => new(At(row, start), At(row, end));

    private static ReplaceScope Scope(int r1, int c1, int r2, int c2)
        => new(At(r1, c1), At(r2, c2));

    [Fact]
    public void A_match_inside_the_scope_is_contained()
    {
        Assert.True(Scope(0, 0, 2, 10).Contains(Match(1, 3, 7)));
    }

    [Fact]
    public void A_match_flush_against_either_edge_is_contained()
    {
        ReplaceScope scope = Scope(1, 4, 1, 12);

        Assert.True(scope.Contains(Match(1, 4, 8)));   // starts exactly at the start
        Assert.True(scope.Contains(Match(1, 8, 12)));  // ends exactly at the end
    }

    [Fact]
    public void A_match_straddling_an_edge_is_not_contained()
    {
        ReplaceScope scope = Scope(1, 4, 1, 12);

        Assert.False(scope.Contains(Match(1, 2, 6)));   // starts before the scope
        Assert.False(scope.Contains(Match(1, 10, 14))); // ends after it
    }

    [Fact]
    public void A_match_on_another_row_entirely_is_not_contained()
    {
        ReplaceScope scope = Scope(1, 0, 1, 20);

        Assert.False(scope.Contains(Match(0, 0, 4)));
        Assert.False(scope.Contains(Match(2, 0, 4)));
    }

    [Fact]
    public void A_longer_replacement_pushes_the_end_right()
    {
        // "cat" -> "kitten" on the scope's own row: the end has to gain three columns
        // or the last match in the selection falls out of bounds.
        ReplaceScope moved = Scope(1, 0, 1, 30).AfterReplacing(Match(1, 4, 7), replacementLength: 6);

        Assert.Equal(At(1, 33), moved.End);
        Assert.Equal(At(1, 0), moved.Start);
    }

    [Fact]
    public void A_shorter_replacement_pulls_the_end_left()
    {
        ReplaceScope moved = Scope(1, 0, 1, 30).AfterReplacing(Match(1, 4, 10), replacementLength: 2);

        Assert.Equal(At(1, 26), moved.End);
    }

    [Fact]
    public void An_edit_on_an_earlier_row_leaves_the_end_alone()
    {
        // Rows never move — the replacement cannot contain a newline — so a scope
        // ending further down keeps its column: the text before that column on *that*
        // row did not change.
        ReplaceScope scope = Scope(0, 0, 5, 12);

        Assert.Equal(scope, scope.AfterReplacing(Match(2, 4, 7), replacementLength: 99));
    }

    [Fact]
    public void The_end_never_retreats_past_the_match_it_just_replaced()
    {
        // A pathological scope ending inside the match: shrinking by the full delta
        // would put End before the replacement that was just written, and Contains
        // would then reject every remaining hit for the wrong reason.
        ReplaceScope moved = Scope(1, 0, 1, 8).AfterReplacing(Match(1, 4, 10), replacementLength: 0);

        Assert.Equal(At(1, 4), moved.End);
    }
}
