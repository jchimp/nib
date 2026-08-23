using System.Reflection;
using System.Text.Json;

namespace Nib.Highlight;

/// <summary>
/// The vendoring manifest, read back at runtime from the same
/// <c>grammars/manifest.json</c> that <c>scripts/fetch-grammars.ps1</c> downloads
/// from. Embedding the file rather than transcribing it into a C# table keeps one
/// source of truth: add a language to the manifest, re-run the script, and both
/// the resources and the detection tables move together.
///
/// Holds the lookup tables detection needs (extension, filename, shebang) and the
/// scope-to-resource-id map the <see cref="GrammarStore"/> needs.
/// </summary>
public sealed class GrammarManifest
{
    private const string ResourceName = "Nib.Resources.manifest.json";

    private readonly Dictionary<string, string> _scopeToId;
    private readonly Dictionary<string, string> _extToScope;
    private readonly Dictionary<string, string> _nameToScope;
    private readonly Dictionary<string, string> _shebangToScope;

    public IReadOnlyList<string> ThemeIds { get; }
    public string DefaultThemeId { get; }

    /// <summary>Theme id → display label ("dark-plus" → "Dark+"), for the message row.</summary>
    public IReadOnlyDictionary<string, string> ThemeLabels { get; }

    private GrammarManifest(
        Dictionary<string, string> scopeToId,
        Dictionary<string, string> extToScope,
        Dictionary<string, string> nameToScope,
        Dictionary<string, string> shebangToScope,
        List<string> themeIds,
        Dictionary<string, string> themeLabels,
        string defaultThemeId)
    {
        _scopeToId = scopeToId;
        _extToScope = extToScope;
        _nameToScope = nameToScope;
        _shebangToScope = shebangToScope;
        ThemeIds = themeIds;
        ThemeLabels = themeLabels;
        DefaultThemeId = defaultThemeId;
    }

    /// <summary>Every scope we ship a grammar for, including dependency-only ones.</summary>
    public IEnumerable<string> AllScopes => _scopeToId.Keys;

    /// <summary>The resource id for a scope, or null if we don't ship that grammar.</summary>
    public string? IdForScope(string scopeName) =>
        _scopeToId.TryGetValue(scopeName, out string? id) ? id : null;

    /// <summary>Scope for a file extension including the dot (".yml"), or null.</summary>
    public string? ScopeForExtension(string extension) =>
        _extToScope.TryGetValue(extension, out string? s) ? s : null;

    /// <summary>Scope for a bare filename ("docker-compose.yml"), or null.</summary>
    public string? ScopeForFilename(string fileName) =>
        _nameToScope.TryGetValue(fileName, out string? s) ? s : null;

    /// <summary>Scope for an interpreter name from a shebang ("python3"), or null.</summary>
    public string? ScopeForShebang(string interpreter) =>
        _shebangToScope.TryGetValue(interpreter, out string? s) ? s : null;

    /// <summary>
    /// Parse the embedded manifest. Throws if it is missing or malformed — that is a
    /// build error, not a runtime condition, and the caller falls back to
    /// <see cref="NullHighlighter"/> rather than letting it escape.
    /// </summary>
    public static GrammarManifest Load()
    {
        using Stream stream = typeof(GrammarManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"missing embedded resource {ResourceName}");
        using JsonDocument doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;

        var scopeToId = new Dictionary<string, string>(StringComparer.Ordinal);
        // Paths and interpreters are matched case-insensitively — Windows filesystems
        // are, and ".YML" is the same file type as ".yml".
        var extToScope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nameToScope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shebangToScope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement g in root.GetProperty("grammars").EnumerateArray())
        {
            string id = g.GetProperty("id").GetString()!;
            string scope = g.GetProperty("scopeName").GetString()!;
            scopeToId[scope] = id;

            // First entry wins on a collision, so manifest order is the tie-break.
            // The dependency-only grammars (the YAML variants) list no extensions and
            // so never become a detection target.
            AddAll(g, "extensions", scope, extToScope);
            AddAll(g, "filenames", scope, nameToScope);
            AddAll(g, "shebangs", scope, shebangToScope);
        }

        var themeIds = new List<string>();
        var themeLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        string? defaultTheme = null;

        foreach (JsonElement t in root.GetProperty("themes").EnumerateArray())
        {
            string id = t.GetProperty("id").GetString()!;
            themeIds.Add(id);
            themeLabels[id] = t.TryGetProperty("label", out JsonElement label)
                ? label.GetString() ?? id
                : id;
            if (t.TryGetProperty("default", out JsonElement d) && d.GetBoolean()) defaultTheme ??= id;
        }

        if (themeIds.Count == 0) throw new InvalidOperationException("manifest lists no themes");

        return new GrammarManifest(
            scopeToId, extToScope, nameToScope, shebangToScope,
            themeIds, themeLabels, defaultTheme ?? themeIds[0]);
    }

    private static void AddAll(JsonElement grammar, string property, string scope, Dictionary<string, string> into)
    {
        if (!grammar.TryGetProperty(property, out JsonElement array)) return;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.GetString() is { Length: > 0 } key) into.TryAdd(key, scope);
        }
    }
}
