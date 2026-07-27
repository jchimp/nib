using Nib.Model;
using Xunit;

namespace Nib.Tests;

public class SelectionTests
{
    private static (Selection Sel, Cursor Cur) Setup(params string[] lines)
    {
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        var buffer = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
        return (new Selection(), new Cursor(buffer));
    }

    [Fact]
    public void No_anchor_means_inactive()
    {
        (Selection sel, Cursor cur) = Setup("hello");
        Assert.False(sel.HasAnchor);
        Assert.False(sel.IsActive(cur));
    }

    [Fact]
    public void Range_is_normalized_when_the_head_is_left_of_the_anchor()
    {
        (Selection sel, Cursor cur) = Setup("hello world");
        cur.MoveTo(0, 8);
        sel.AnchorAt(cur);   // anchor at col 8
        cur.MoveTo(0, 2);    // head dragged left to col 2
        (TextPosition start, TextPosition end) = sel.Range(cur);
        Assert.Equal(new TextPosition(0, 2), start);
        Assert.Equal(new TextPosition(0, 8), end);
        Assert.True(sel.IsActive(cur));
    }

    [Fact]
    public void A_collapsed_selection_is_not_active()
    {
        (Selection sel, Cursor cur) = Setup("hello");
        cur.MoveTo(0, 3);
        sel.AnchorAt(cur); // anchor and head coincide
        Assert.False(sel.IsActive(cur));
    }

    [Fact]
    public void ContainsRow_spans_partial_first_full_middle_and_partial_last()
    {
        (Selection sel, Cursor cur) = Setup("first", "middle", "last");
        cur.MoveTo(0, 2);
        sel.AnchorAt(cur);
        cur.MoveTo(2, 3); // select from (0,2) to (2,3)

        Assert.True(sel.ContainsRow(0, cur, out int s0, out int e0));
        Assert.Equal(2, s0);
        Assert.Equal(int.MaxValue, e0); // partial first row: from col 2 to EOL

        Assert.True(sel.ContainsRow(1, cur, out int s1, out int e1));
        Assert.Equal(0, s1);
        Assert.Equal(int.MaxValue, e1); // full middle row

        Assert.True(sel.ContainsRow(2, cur, out int s2, out int e2));
        Assert.Equal(0, s2);
        Assert.Equal(3, e2); // partial last row: up to col 3

        Assert.False(sel.ContainsRow(3, cur, out _, out _)); // outside the span
    }

    [Fact]
    public void Clear_drops_the_anchor()
    {
        (Selection sel, Cursor cur) = Setup("hello");
        cur.MoveTo(0, 1);
        sel.AnchorAt(cur);
        cur.MoveTo(0, 4);
        Assert.True(sel.IsActive(cur));
        sel.Clear();
        Assert.False(sel.IsActive(cur));
        Assert.False(sel.HasAnchor);
    }
}
