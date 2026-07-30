using System.Diagnostics;
using Nib.Model;
using Nib.Terminal;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Nib.Highlight;

/// <summary>
/// The real highlighter: a per-line token cache over TextMateSharp, kept in step
/// with the buffer and re-tokenized incrementally.
///
/// TextMateSharp's API is line-at-a-time with an explicit carry state — the rule
/// stack a line ends in becomes the next line's starting state. Caching that state
/// per line is what makes an edit cheap: re-tokenize forward from the edited line
/// and <b>stop as soon as a line's new carry state equals its cached one</b>,
/// because from there down nothing can have changed. Typing inside a string on
/// line 3 of a 20k-line file re-tokenizes line 3, sees the same state coming out,
/// and stops. That single rule is the difference between highlighting being free
/// and being unusable.
///
/// The walk is strictly sequential from the top of the file, because line N's
/// state depends on N-1. Rows are therefore coloured in file order, and a region
/// the walk has not reached yet renders plain until it does. That is the
/// documented first-paint behaviour, not a bug.
/// </summary>
public sealed class TextMateHighlighter : IHighlighter
{
    // Oniguruma will backtrack effectively forever on a minified line. VS Code caps
    // it for the same reason; a line that overruns keeps whatever it had and the
    // editor stays responsive. Never TimeSpan.MaxValue.
    private static readonly TimeSpan MaxTokenizeTime = TimeSpan.FromMilliseconds(50);

    // How long one frame may spend colouring rows below the visible window. The
    // visible window itself is never budgeted — it has to be right before we paint.
    //
    // This is the whole per-keystroke cost, because repairing the edit converges in
    // a line or two and the rest of the frame goes here. The ROADMAP's budget for an
    // edit is 5 ms; 1.5 leaves room for the repair and the render underneath it, and
    // still fills a 20k-line file within a few hundred frames of scrolling.
    private static readonly TimeSpan LookaheadBudget = TimeSpan.FromMilliseconds(1.5);

    private static readonly IReadOnlyList<StyledSpan> NoSpans = [];

    private sealed class LineState
    {
        public IStateStack? Out;
        public IReadOnlyList<StyledSpan>? Spans;
        public bool Valid;
    }

    private readonly TextBuffer _buffer;
    private readonly Registry _registry;
    private readonly GrammarStore _store;
    private readonly GrammarManifest _manifest;
    private readonly IGrammar _grammar;
    private readonly List<LineState> _states;

    private ThemeMap _theme;
    private int _themeIndex;

    // How far the sequential walk has reached: rows [0, _frontier) have been
    // tokenized. Rows beyond it have never been looked at and render plain.
    private int _frontier;

    // Lowest row an edit made stale, or int.MaxValue when nothing is pending.
    // Distinct from _frontier because the two mean different work: _dirty is a
    // repair inside already-tokenized territory that stops at convergence,
    // _frontier is new ground that has to be covered line by line. Conflating them
    // makes the walk redo hundreds of already-correct rows after every keystroke.
    private int _dirty;

    /// <summary>
    /// Lines handed to the grammar since construction. A test affordance: the
    /// convergence rule is only observable as "how few lines did that edit cost",
    /// and asserting on wall-clock time would be flaky.
    /// </summary>
    internal int LinesTokenized { get; private set; }

    /// <summary>Rows in the cache. Must always equal the buffer's line count.</summary>
    internal int CachedRows => _states.Count;

    private TextMateHighlighter(
        TextBuffer buffer, Registry registry, GrammarStore store,
        GrammarManifest manifest, IGrammar grammar, ThemeMap theme, int themeIndex)
    {
        _buffer = buffer;
        _registry = registry;
        _store = store;
        _manifest = manifest;
        _grammar = grammar;
        _theme = theme;
        _themeIndex = themeIndex;

        _states = new List<LineState>(buffer.LineCount);
        for (int i = 0; i < buffer.LineCount; i++) _states.Add(new LineState());
        _frontier = 0;
        _dirty = int.MaxValue;

        buffer.LinesChanged += Invalidate;
    }

    /// <summary>
    /// Build a highlighter for <paramref name="buffer"/>, or return
    /// <see cref="NullHighlighter"/> when there is nothing to highlight with: no
    /// grammar for the file type, a theme that would not load, or a broken
    /// resource. Never throws — an editor that refuses to open a file because it
    /// could not colour it would be worse than one with no colour at all.
    /// </summary>
    public static IHighlighter Create(TextBuffer buffer, string? themeId = null, string? scopeOverride = null)
    {
        try
        {
            GrammarManifest manifest = GrammarManifest.Load();
            var store = new GrammarStore(manifest);
            var detector = new LanguageDetector(manifest);

            string? firstLine = buffer.LineCount > 0 ? buffer.GetLine(0) : null;
            if (detector.Detect(buffer.Path, firstLine, scopeOverride) is not { } scope) return NullHighlighter.Instance;

            var registry = new Registry(store);
            IGrammar? grammar = registry.LoadGrammar(scope);
            if (grammar is null) return NullHighlighter.Instance;

            int index = ResolveThemeIndex(manifest, themeId);
            if (LoadTheme(registry, store, manifest, index) is not { } theme) return NullHighlighter.Instance;

            return new TextMateHighlighter(buffer, registry, store, manifest, grammar, theme, index);
        }
        catch (Exception)
        {
            return NullHighlighter.Instance;
        }
    }

    // ---- IHighlighter -------------------------------------------------------

    public string ThemeName => _theme.Name;

    public Color? SelectionBackground => _theme.SelectionBackground;

    public IReadOnlyList<StyledSpan>? Spans(int row)
    {
        if ((uint)row >= (uint)_states.Count) return null;
        LineState state = _states[row];
        return state.Valid ? state.Spans : null;
    }

    public void SetTheme(string themeId)
    {
        int index = _manifest.ThemeIds.ToList().IndexOf(themeId);
        if (index < 0) return;
        ApplyTheme(index);
    }

    /// <summary>Move to the next theme in manifest order and return its display name.</summary>
    public string CycleTheme()
    {
        ApplyTheme((_themeIndex + 1) % _manifest.ThemeIds.Count);
        return ThemeName;
    }

    public void Invalidate(int row, int removed, int inserted)
    {
        if (row < 0) return;

        // Splice the cache to match the buffer. Keeping the rows below an edit is
        // the whole point: their cached carry state is what the walk compares
        // against to decide it can stop.
        int start = Math.Min(row, _states.Count);
        int drop = Math.Clamp(removed, 0, _states.Count - start);
        if (drop > 0) _states.RemoveRange(start, drop);
        for (int i = 0; i < inserted; i++) _states.Insert(start + i, new LineState());

        // Cheap insurance against any arithmetic drift: the cache must stay
        // index-aligned with the buffer or every row below is coloured wrong.
        Realign();

        // Rows below an edit keep their tokens and simply move, so the frontier
        // moves with them rather than collapsing back to the edit.
        if (start < _frontier) _frontier += inserted - drop;
        _frontier = Math.Clamp(_frontier, 0, _states.Count);

        _dirty = Math.Min(_dirty, start);
    }

    public void TokenizeWindow(int firstRow, int rowCount)
    {
        // Everything down to the bottom of the visible window must be right before
        // the frame is painted; only what lies beyond it is budgeted.
        int windowEnd = Math.Min(_states.Count, Math.Max(0, firstRow) + Math.Max(0, rowCount));
        Walk(windowEnd, budget: null);
        Walk(_states.Count, budget: LookaheadBudget);
    }

    /// <summary>
    /// Nothing to drain. Tokenization runs on the loop thread inside
    /// <see cref="TokenizeWindow"/>, deliberately: a worker would have to read
    /// <see cref="TextBuffer"/> while the loop is editing it, and Model/ is not
    /// thread-safe. Kept on the interface so a future engine can background its
    /// work without the loop changing.
    /// </summary>
    public void Pump() { }

    // ---- the walk -----------------------------------------------------------

    // Tokenize forward, repairing the dirty region first and then extending the
    // frontier, stopping at `until` or when the budget runs out.
    private void Walk(int until, TimeSpan? budget)
    {
        int row = Math.Min(_dirty, _frontier);
        if (row >= until) return;

        Stopwatch? clock = budget is null ? null : Stopwatch.StartNew();

        while (row < until && row < _states.Count)
        {
            LineState state = _states[row];
            IStateStack? cachedOut = state.Out;
            bool wasValid = state.Valid;

            Tokenize(row, row == 0 ? null : _states[row - 1].Out);
            row++;
            if (row > _frontier) _frontier = row;

            // Rows up to here are repaired. Recording that as we go is what lets a
            // budgeted walk resume where it stopped; without it the next frame
            // starts over from the edit and the walk never reaches the end of a
            // large file.
            if (_dirty < row) _dirty = row;

            // Converged: this line ends in the state it used to, so every line below
            // starts where it did and its cached tokens still stand. The repair is
            // done — skip whatever is already tokenized and carry on extending.
            if (wasValid && cachedOut is not null && StateEquivalence.AreEquivalent(cachedOut, _states[row - 1].Out))
            {
                _dirty = int.MaxValue;
                if (row < _frontier) row = _frontier;
            }

            if (clock is not null && clock.Elapsed >= budget!.Value) return;
        }

        if (row >= _dirty) _dirty = int.MaxValue;
    }

    private void Tokenize(int row, IStateStack? stateIn)
    {
        LineState state = _states[row];
        string text = _buffer.GetLine(row);
        LinesTokenized++;

        try
        {
            ITokenizeLineResult result = _grammar.TokenizeLine(text, stateIn, MaxTokenizeTime);
            state.Out = result.RuleStack;
            state.Spans = BuildSpans(result.Tokens, text.Length);
        }
        catch (Exception)
        {
            // A grammar that throws on one pathological line must not take the
            // editor with it. Render the line plain and carry the state through.
            state.Out = stateIn;
            state.Spans = NoSpans;
        }

        state.Valid = true;
    }

    // Tokens tile the line end to end, most of them sharing the root scope and so
    // the default colour. Merge equal-coloured neighbours and drop a run that is
    // entirely default — the view already paints unspanned text plain, and a
    // shorter list is less work on the render path.
    private IReadOnlyList<StyledSpan> BuildSpans(IToken[] tokens, int lineLength)
    {
        if (tokens.Length == 0 || lineLength == 0) return NoSpans;

        List<StyledSpan>? spans = null;
        int runStart = -1, runEnd = -1;
        Color runColor = Color.Default;

        foreach (IToken token in tokens)
        {
            int start = Math.Clamp(token.StartIndex, 0, lineLength);
            int end = Math.Clamp(token.EndIndex, start, lineLength);
            if (end == start) continue;

            Color color = _theme.Foreground(token.Scopes);

            if (runStart >= 0 && start == runEnd && color == runColor) { runEnd = end; continue; }

            if (runStart >= 0 && !runColor.IsDefault)
                (spans ??= []).Add(new StyledSpan(runStart, runEnd - runStart, runColor));

            runStart = start;
            runEnd = end;
            runColor = color;
        }

        if (runStart >= 0 && !runColor.IsDefault)
            (spans ??= []).Add(new StyledSpan(runStart, runEnd - runStart, runColor));

        return spans ?? NoSpans;
    }

    // ---- themes -------------------------------------------------------------

    private void ApplyTheme(int index)
    {
        if (LoadTheme(_registry, _store, _manifest, index) is not { } theme) return;

        _theme = theme;
        _themeIndex = index;

        // Spans carry resolved colours, so they all have to be rebuilt. The carry
        // states are still correct, but re-deriving spans without re-tokenizing
        // would mean caching every token's scope list — far more memory than a
        // re-walk costs on a config file, for something the user does rarely.
        foreach (LineState state in _states) state.Valid = false;
        _frontier = 0;
        _dirty = int.MaxValue;
    }

    private static ThemeMap? LoadTheme(Registry registry, GrammarStore store, GrammarManifest manifest, int index)
    {
        string id = manifest.ThemeIds[index];
        IRawTheme? raw = store.GetTheme(id);
        if (raw is null) return null;

        registry.SetTheme(raw);
        Theme? theme = registry.GetTheme();
        if (theme is null) return null;

        return new ThemeMap(theme, manifest.ThemeLabels.TryGetValue(id, out string? label) ? label : id);
    }

    private static int ResolveThemeIndex(GrammarManifest manifest, string? themeId)
    {
        if (!string.IsNullOrEmpty(themeId))
        {
            for (int i = 0; i < manifest.ThemeIds.Count; i++)
            {
                if (string.Equals(manifest.ThemeIds[i], themeId, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }

        for (int i = 0; i < manifest.ThemeIds.Count; i++)
        {
            if (string.Equals(manifest.ThemeIds[i], manifest.DefaultThemeId, StringComparison.Ordinal)) return i;
        }
        return 0;
    }

    // Bring the cache back to one entry per buffer row. Only ever a no-op in
    // practice; it exists so a miscounted edit degrades to a repaint instead of
    // an off-by-one that colours the rest of the file wrong.
    private void Realign()
    {
        int want = _buffer.LineCount;
        while (_states.Count > want) _states.RemoveAt(_states.Count - 1);
        while (_states.Count < want) _states.Add(new LineState());
    }
}
