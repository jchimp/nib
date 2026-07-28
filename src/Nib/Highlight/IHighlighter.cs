using Nib.Terminal;

namespace Nib.Highlight;

/// <summary>
/// A run of characters on one line sharing a foreground colour. Start and Length
/// are UTF-16 indices into <see cref="Model.Line.Text"/> — the same coordinates the
/// cursor and selection use, so the view can walk spans and characters together.
/// Spans for a row are ordered and non-overlapping.
/// </summary>
public readonly record struct StyledSpan(int Start, int Length, Color Fg);

/// <summary>
/// Source of per-line colour for the view. The interface exists so the engine is
/// swappable: if TextMateSharp's native Oniguruma dependency ever proves unstable
/// under single-file publish, a hand-rolled tokenizer for the config formats drops
/// in behind this without the view or the loop noticing.
///
/// The contract is deliberately pull-based and non-blocking. <see cref="Spans"/>
/// never tokenizes — it returns what is already cached, or null — because it is
/// called from the render path and a regex engine has no business there.
/// </summary>
public interface IHighlighter
{
    /// <summary>Display name of the active theme, for the message row.</summary>
    string ThemeName { get; }

    /// <summary>The theme's selection background, or null to keep the view's own default.</summary>
    Color? SelectionBackground { get; }

    /// <summary>Switch themes in place. Colours change; cached tokens stay valid.</summary>
    void SetTheme(string themeId);

    /// <summary>
    /// Cached spans for a row, or null when the row has not been tokenized yet.
    /// Null means "paint it plain" — first paint is uncolored by design.
    /// </summary>
    IReadOnlyList<StyledSpan>? Spans(int row);

    /// <summary>
    /// The buffer changed at <paramref name="row"/>: <paramref name="removed"/> lines
    /// went away and <paramref name="inserted"/> took their place. Shifts the cache to
    /// match and marks the row dirty.
    /// </summary>
    void Invalidate(int row, int removed, int inserted);

    /// <summary>
    /// Bring the visible rows up to date synchronously, then walk forward from any
    /// dirty row until the carry state converges. Called once per frame, before paint.
    /// </summary>
    void TokenizeWindow(int firstRow, int rowCount);

    /// <summary>Drain whatever the background tokenizer finished since the last frame.</summary>
    void Pump();
}

/// <summary>
/// The no-colour path: no grammar matched the file, the theme failed to load, or
/// the terminal never got VT processing. Everything renders plain, no error.
/// </summary>
public sealed class NullHighlighter : IHighlighter
{
    public static readonly NullHighlighter Instance = new();

    public string ThemeName => "none";
    public Color? SelectionBackground => null;

    public void SetTheme(string themeId) { }
    public IReadOnlyList<StyledSpan>? Spans(int row) => null;
    public void Invalidate(int row, int removed, int inserted) { }
    public void TokenizeWindow(int firstRow, int rowCount) { }
    public void Pump() { }
}
