using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;

namespace Nib.Highlight;

/// <summary>
/// Structural comparison of two tokenizer carry states.
///
/// The whole incremental story rests on asking "does this line still end in the
/// state it used to?", and <see cref="StateStack.Equals"/> cannot answer it.
/// It returns false for two stacks that are identical in every field we can
/// observe — same depth, same rule id, same end rule — because somewhere in the
/// chain it falls back on reference identity.
///
/// The effect is invisible for most grammars, which is what makes it dangerous:
/// when a line leaves the state untouched TextMateSharp hands back the *same*
/// stack object, so reference equality happens to be true and JSON, INI and Python
/// converge after two lines. YAML rebuilds its stack on every line, so it never
/// converged at all and every keystroke re-tokenized the whole visible window.
/// YAML being the one language a config editor exists for, that is worth fixing
/// rather than absorbing.
///
/// Compared here: the rule identity of each frame in the chain, and the scope
/// paths attached to it — the same fields vscode-textmate's own structural
/// equality uses. Deliberately not compared: the enter and anchor positions, which
/// record where on a line a rule was entered and legitimately differ between two
/// states that mean the same thing.
/// </summary>
internal static class StateEquivalence
{
    public static bool AreEquivalent(IStateStack? left, IStateStack? right)
    {
        if (ReferenceEquals(left, right)) return true;

        StateStack? a = left as StateStack;
        StateStack? b = right as StateStack;

        while (a is not null && b is not null)
        {
            if (ReferenceEquals(a, b)) return true;

            if (a.Depth != b.Depth) return false;
            if (a.RuleId.Id != b.RuleId.Id) return false;
            if (a.BeginRuleCapturedEOL != b.BeginRuleCapturedEOL) return false;
            if (!string.Equals(a.EndRule, b.EndRule, StringComparison.Ordinal)) return false;
            if (!ScopesMatch(a.NameScopesList, b.NameScopesList)) return false;
            if (!ScopesMatch(a.ContentNameScopesList, b.ContentNameScopesList)) return false;

            a = a.Parent;
            b = b.Parent;
        }

        return a is null && b is null;
    }

    // ScopePath is the whole path, so one string compare covers the chain. The
    // attributes carry the theme-resolved styling; if those differ the colours
    // would too, so a match has to include them.
    private static bool ScopesMatch(AttributedScopeStack? a, AttributedScopeStack? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.TokenAttributes == b.TokenAttributes
            && string.Equals(a.ScopePath, b.ScopePath, StringComparison.Ordinal);
    }
}
