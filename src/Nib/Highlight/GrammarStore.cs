using System.IO.Compression;
using System.Reflection;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Nib.Highlight;

/// <summary>
/// Serves the vendored grammars and themes to TextMateSharp's registry out of
/// gzipped embedded resources, decompressing each one the first time its scope is
/// asked for.
///
/// Lazy matters more than it looks: the engine calls <see cref="GetGrammar"/> for
/// every scope a grammar references, so opening a YAML file pulls in the six-file
/// dispatcher chain and nothing else, and opening an .ini file costs 735 bytes of
/// inflate. Eagerly loading all nineteen would put ~777 KB of JSON parsing on the
/// startup path we are trying to keep under 100 ms.
/// </summary>
public sealed class GrammarStore : IRegistryOptions
{
    private const string GrammarPrefix = "Nib.Resources.Grammars.";
    private const string ThemePrefix = "Nib.Resources.Themes.";

    private static readonly Assembly Owner = typeof(GrammarStore).Assembly;

    private readonly GrammarManifest _manifest;
    private readonly Dictionary<string, IRawGrammar?> _grammars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IRawTheme?> _themes = new(StringComparer.Ordinal);

    public GrammarStore(GrammarManifest manifest) => _manifest = manifest;

    /// <summary>Theme ids in manifest order, for cycling.</summary>
    public IReadOnlyList<string> ThemeIds => _manifest.ThemeIds;

    /// <summary>The manifest's default theme id (Dark+ unless the manifest says otherwise).</summary>
    public string DefaultThemeId => _manifest.DefaultThemeId;

    // ---- IRegistryOptions ---------------------------------------------------

    public IRawGrammar? GetGrammar(string scopeName)
    {
        if (_grammars.TryGetValue(scopeName, out IRawGrammar? cached)) return cached;

        IRawGrammar? grammar = null;
        if (_manifest.IdForScope(scopeName) is { } id)
        {
            using StreamReader? reader = OpenResource(GrammarPrefix + id + ".json.gz");
            // A grammar that fails to read is not fatal: the engine treats an
            // unresolved include as plain text, which is the documented
            // degradation for the 61 scopes markdown references and we don't ship.
            if (reader is not null) grammar = TryRead(() => GrammarReader.ReadGrammarSync(reader));
            // Must run before the first tokenize compiles the rules. See RawGrammarFixup.
            if (grammar is not null) RawGrammarFixup.Apply(grammar);
        }

        _grammars[scopeName] = grammar; // negative results cached too — misses are the common case
        return grammar;
    }

    public IRawTheme? GetTheme(string themeId)
    {
        if (_themes.TryGetValue(themeId, out IRawTheme? cached)) return cached;

        IRawTheme? theme = null;
        using (StreamReader? reader = OpenResource(ThemePrefix + themeId + ".json.gz"))
        {
            if (reader is not null) theme = TryRead(() => ThemeReader.ReadThemeSync(reader));
        }

        _themes[themeId] = theme;
        return theme;
    }

    public IRawTheme? GetDefaultTheme() => GetTheme(_manifest.DefaultThemeId);

    /// <summary>
    /// Grammar injections — extra grammars that graft onto another's scope. None of
    /// the nineteen we vendor declare any, so this is always empty; the interface
    /// requires it.
    /// </summary>
    public ICollection<string> GetInjections(string scopeName) => [];

    // ---- resources ----------------------------------------------------------

    // Inflate fully into memory before parsing. TextMateSharp's JSON reader walks
    // the StreamReader in a way that a non-seekable GZipStream does not survive —
    // handing it the gzip stream directly loses characters on larger grammars and
    // fails as a cast error deep inside rule compilation, nowhere near the cause.
    // The buffers are ~100 KB at worst and are freed as soon as parsing returns.
    private static StreamReader? OpenResource(string logicalName)
    {
        using Stream? raw = Owner.GetManifestResourceStream(logicalName);
        if (raw is null) return null;

        var buffer = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionMode.Decompress)) gz.CopyTo(buffer);
        buffer.Position = 0;
        return new StreamReader(buffer);
    }

    // A malformed or truncated resource is a vendoring bug, not a user problem.
    // Degrade to no highlighting for that scope rather than taking the editor down.
    private static T? TryRead<T>(Func<T> read) where T : class
    {
        try { return read(); }
        catch (Exception) { return null; }
    }
}
