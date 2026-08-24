namespace Nib.Model;

/// <summary>
/// The span a replace pass is confined to — the selection that was live when ^R was
/// pressed. Console-free like the rest of <c>Model/</c>, because the arithmetic below
/// is the part of replace-in-selection that has edge cases and the editor loop is the
/// part that cannot be tested.
///
/// <b>Replacements never move a row.</b> The term and the replacement both come from a
/// one-line prompt, and <see cref="TextSearch"/> matches never span a line break — so a
/// replacement can only ever change the length of the row it lands on. That is what
/// lets <see cref="AfterReplacing"/> be a column adjustment rather than a full remap of
/// the end position. If multi-line terms ever arrive, this struct is the first thing
/// that breaks.
/// </summary>
public readonly record struct ReplaceScope(TextPosition Start, TextPosition End)
{
    /// <summary>Whether the match lies wholly inside the scope. A hit straddling either edge does not.</summary>
    public bool Contains(SearchMatch match) => match.Start >= Start && match.End <= End;

    /// <summary>
    /// The scope after one match inside it has been replaced. The end shifts by the
    /// difference in length, but only when it sits on the row the edit happened on —
    /// a scope ending further down the file keeps its column, because the text before
    /// that column on *that* row did not change.
    /// </summary>
    public ReplaceScope AfterReplacing(SearchMatch match, int replacementLength)
    {
        if (End.Row != match.Start.Row) return this;

        int delta = replacementLength - (match.End.Col - match.Start.Col);
        return this with { End = new TextPosition(End.Row, Math.Max(match.Start.Col, End.Col + delta)) };
    }
}
