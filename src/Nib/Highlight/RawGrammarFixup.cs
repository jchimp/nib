namespace Nib.Highlight;

/// <summary>
/// Repairs two malformations in the grammars VS Code ships that its own tokenizer
/// tolerates and TextMateSharp does not.
///
/// Both fail the same miserable way: lazily, on the first tokenize, as an
/// exception thrown from deep inside rule compilation with nothing in the message
/// naming the grammar responsible. The symptom is that XML, all six YAML files,
/// and Markdown break while the other twelve work — Markdown only because
/// compiling it resolves its fenced-code include of <c>text.xml</c>.
///
/// **Stray keys in a captures map.** A <c>captures</c> map keys rules by capture
/// group number. All three YAML variants park a <c>"comment"</c> holding a spec
/// URL in one, and XML's JSP comment rule has <c>"end"</c> and <c>"name"</c>
/// nested inside <c>captures</c> where they plainly belong beside it. VS Code only
/// ever looks up numbered groups so the extras are inert; TextMateSharp iterates
/// every key and casts each value to <c>IRawRule</c>, so a string throws
/// <c>InvalidCastException</c>. Real rule keys are lifted to the parent (which is
/// what XML meant, and it restores the rule's missing <c>end</c>); documentation
/// keys are dropped.
///
/// **A <c>begin</c> with no <c>end</c>.** XML's bad-comment rule has one. TextMate
/// treats a pattern with no end as a match rule; TextMateSharp builds a begin/end
/// rule and hands a null pattern to Oniguruma, which throws
/// <c>ArgumentNullException</c>. Rewriting <c>begin</c> to <c>match</c> is what the
/// author meant and what VS Code effectively does.
///
/// Fixing this here rather than in <c>scripts/fetch-grammars.ps1</c> keeps the
/// vendored <c>.gz</c> files byte-identical to upstream, so a diff after
/// re-fetching shows real upstream change and not our edits. The walk runs once
/// per grammar on its lazy load, over a tree the JSON parser has just walked.
/// </summary>
internal static class RawGrammarFixup
{
    private static readonly string[] CaptureKeys =
        ["captures", "beginCaptures", "endCaptures", "whileCaptures"];

    // Keys that are meaningful on a rule, and so are a misplacement worth rescuing
    // rather than a comment worth dropping.
    private static readonly HashSet<string> RuleKeys = new(StringComparer.Ordinal)
    {
        "name", "contentName", "begin", "end", "while", "match", "patterns", "applyEndPatternLast",
    };

    public static void Apply(object? node)
    {
        switch (node)
        {
            case IDictionary<string, object> map:
                foreach (string captureKey in CaptureKeys)
                {
                    if (map.TryGetValue(captureKey, out object? value) && value is IDictionary<string, object> captures)
                        Rehome(captures, map);
                }

                BeginWithoutEndBecomesMatch(map);

                // ToArray: recursion mutates nested maps, and the parser's
                // dictionaries are the live ones.
                foreach (object? child in map.Values.ToArray()) Apply(child);
                break;

            case IEnumerable<object> list:
                foreach (object? child in list) Apply(child);
                break;
        }
    }

    // Move non-numeric keys out of a captures map: real rule keys go up to the
    // parent rule unless it already has its own, everything else is dropped.
    private static void Rehome(IDictionary<string, object> captures, IDictionary<string, object> parent)
    {
        List<string>? strays = null;
        foreach (string key in captures.Keys)
        {
            if (IsCaptureNumber(key)) continue;
            (strays ??= []).Add(key);
        }
        if (strays is null) return;

        foreach (string key in strays)
        {
            object value = captures[key];
            captures.Remove(key);
            if (RuleKeys.Contains(key) && !parent.ContainsKey(key)) parent[key] = value;
        }
    }

    private static void BeginWithoutEndBecomesMatch(IDictionary<string, object> rule)
    {
        if (!rule.TryGetValue("begin", out object? begin)) return;
        if (rule.ContainsKey("end") || rule.ContainsKey("while")) return;
        if (rule.ContainsKey("match")) return;

        rule.Remove("begin");
        rule["match"] = begin;

        // A match rule has no begin/end halves, so the captures that addressed them
        // collapse into the one captures map.
        if (rule.TryGetValue("beginCaptures", out object? beginCaptures) && !rule.ContainsKey("captures"))
            rule["captures"] = beginCaptures;
        rule.Remove("beginCaptures");
        rule.Remove("endCaptures");
    }

    private static bool IsCaptureNumber(string key)
    {
        if (key.Length == 0) return false;
        foreach (char c in key)
        {
            if (c is < '0' or > '9') return false;
        }
        return true;
    }
}
