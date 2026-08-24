namespace Nib.Model;

/// <summary>
/// What to look for. Carried as a value rather than as three parameters so the
/// toggles cannot drift apart from the term between the prompt and the scan.
/// </summary>
public readonly record struct SearchQuery(string Text, bool MatchCase, bool WholeWord);

/// <summary>One hit, as the half-open span [Start, End) the caller can select or replace.</summary>
public readonly record struct SearchMatch(TextPosition Start, TextPosition End);

/// <summary>
/// Finding text in a buffer. Console-free by rule, like the rest of <c>Model/</c> —
/// the whole of search is testable with nothing but the BCL.
///
/// <b>Matches never span a line break.</b> This is a config-file editor and nano is
/// line-based for the same reason: it keeps the scan a plain IndexOf per row, and a
/// multi-line term is not something you type into a one-line prompt.
///
/// Wrapping is this class's job rather than the caller's, and the <c>wrapped</c>
/// flag exists only so the message row can say that it happened.
/// </summary>
public static class TextSearch
{
    /// <summary>
    /// The first match at or after <paramref name="from"/>, wrapping to the top of
    /// the buffer if it runs out. Null when the term does not occur at all — and in
    /// that case <paramref name="wrapped"/> is false, so "not found" never claims to
    /// have wrapped on the way to saying nothing was there.
    /// </summary>
    public static SearchMatch? FindNext(TextBuffer buffer, TextPosition from, SearchQuery query, out bool wrapped)
    {
        wrapped = false;
        if (query.Text.Length == 0) return null;

        (int startRow, int startCol) = Anchor(buffer, from);

        for (int row = startRow; row < buffer.LineCount; row++)
        {
            int hit = IndexIn(buffer.GetLine(row), row == startRow ? startCol : 0, query);
            if (hit >= 0) return At(row, hit, query);
        }

        // Round the top. On the starting row only a hit *before* where we began is
        // new; anything at or after it was already covered by the pass above.
        wrapped = true;
        for (int row = 0; row <= startRow; row++)
        {
            string line = buffer.GetLine(row);
            int hit = IndexIn(line, 0, query);
            if (hit < 0) continue;
            if (row == startRow && hit >= startCol) break;
            return At(row, hit, query);
        }

        wrapped = false;
        return null;
    }

    /// <summary>
    /// The last match starting strictly before <paramref name="from"/>, wrapping to
    /// the bottom of the buffer if it runs out.
    /// </summary>
    public static SearchMatch? FindPrevious(TextBuffer buffer, TextPosition from, SearchQuery query, out bool wrapped)
    {
        wrapped = false;
        if (query.Text.Length == 0) return null;

        (int startRow, int startCol) = Anchor(buffer, from);

        for (int row = startRow; row >= 0; row--)
        {
            string line = buffer.GetLine(row);
            int hit = LastIndexIn(line, row == startRow ? startCol : line.Length + 1, query);
            if (hit >= 0) return At(row, hit, query);
        }

        // Round the bottom. On the starting row only a hit at or after where we began
        // is new.
        wrapped = true;
        for (int row = buffer.LineCount - 1; row >= startRow; row--)
        {
            string line = buffer.GetLine(row);
            int hit = LastIndexIn(line, line.Length + 1, query);
            if (hit < 0) continue;
            if (row == startRow && hit < startCol) break;
            return At(row, hit, query);
        }

        wrapped = false;
        return null;
    }

    /// <summary>
    /// Every match in the buffer, in order, at or after <paramref name="from"/> and
    /// ending at or before <paramref name="to"/>. Matches never overlap: the scan
    /// resumes past each hit.
    ///
    /// <paramref name="to"/> is what makes replace-in-selection possible: a match that
    /// straddles the bound is outside it, because replacing it would rewrite text the
    /// user did not select.
    /// </summary>
    public static List<SearchMatch> FindAll(TextBuffer buffer, SearchQuery query,
                                            TextPosition? from = null, TextPosition? to = null)
    {
        var found = new List<SearchMatch>();
        if (query.Text.Length == 0) return found;

        (int startRow, int startCol) = from is { } p ? Anchor(buffer, p) : (0, 0);

        int lastRow = buffer.LineCount - 1;
        TextPosition? limit = null;
        if (to is { } t)
        {
            (int row, int col) = Anchor(buffer, t);
            limit = new TextPosition(row, col);
            lastRow = row;
        }

        for (int row = startRow; row <= lastRow; row++)
        {
            string line = buffer.GetLine(row);
            int i = row == startRow ? startCol : 0;
            while (true)
            {
                int hit = IndexIn(line, i, query);
                if (hit < 0) break;

                SearchMatch match = At(row, hit, query);
                if (limit is { } end && match.End > end) return found; // ordered: nothing later fits either

                found.Add(match);
                i = hit + query.Text.Length;
            }
        }

        return found;
    }

    // A position the buffer actually has. Search is driven by the caret and by a
    // remembered query, either of which can name a row an edit has since removed.
    private static (int Row, int Col) Anchor(TextBuffer buffer, TextPosition p)
    {
        int row = Math.Clamp(p.Row, 0, buffer.LineCount - 1);
        return (row, Math.Clamp(p.Col, 0, buffer.LineLength(row)));
    }

    private static SearchMatch At(int row, int col, SearchQuery query)
        => new(new TextPosition(row, col), new TextPosition(row, col + query.Text.Length));

    // The first hit at or after startIndex, honouring the whole-word toggle.
    private static int IndexIn(string line, int startIndex, SearchQuery query)
    {
        if (startIndex > line.Length) return -1;
        StringComparison cmp = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int i = startIndex;
        while (i <= line.Length - query.Text.Length)
        {
            int hit = line.IndexOf(query.Text, i, cmp);
            if (hit < 0) return -1;
            if (!query.WholeWord || IsWholeWord(line, hit, query.Text.Length)) return hit;
            i = hit + 1; // a hit rejected for its boundaries may still overlap a real one
        }
        return -1;
    }

    // The last hit starting before beforeIndex. Walks forward keeping the best rather
    // than calling LastIndexOf: that overload's startIndex/count semantics run the
    // opposite way from IndexOf's, and getting them subtly wrong shows up on about one
    // line in a hundred. Lines are short; correctness is worth the extra scan.
    private static int LastIndexIn(string line, int beforeIndex, SearchQuery query)
    {
        int best = -1;
        int i = 0;
        while (true)
        {
            int hit = IndexIn(line, i, query);
            if (hit < 0 || hit >= beforeIndex) break;
            best = hit;
            i = hit + 1;
        }
        return best;
    }

    // Word characters here are letters, digits and underscore — deliberately *not*
    // the whitespace-delimited definition Cursor.WordLeft/WordRight use for ^arrow
    // movement. Under that rule "foo," is a single word, so a whole-word search for
    // "foo" would refuse to match it, which is not what anyone means by the toggle.
    private static bool IsWholeWord(string line, int index, int length)
    {
        bool leftOk = index == 0 || !IsWordChar(line[index - 1]);
        bool rightOk = index + length >= line.Length || !IsWordChar(line[index + length]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
