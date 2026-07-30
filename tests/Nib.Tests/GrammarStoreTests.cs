using Nib.Highlight;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The vendored resources actually load, compile, and produce tokens.
///
/// Worth its own suite because every way this fails is silent: an absent resource,
/// a grammar whose scope nothing resolves, an upstream malformation TextMateSharp
/// rejects — all of them surface to a user as "the file just isn't coloured", with
/// no error anywhere. Rule compilation is also lazy, so loading a grammar proves
/// nothing; each one has to be made to tokenize a line.
/// </summary>
public class GrammarStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private static (GrammarManifest Manifest, Registry Registry) Setup()
    {
        GrammarManifest manifest = GrammarManifest.Load();
        return (manifest, new Registry(new GrammarStore(manifest)));
    }

    [Fact]
    public void Manifest_ships_the_expected_resource_counts()
    {
        (GrammarManifest manifest, _) = Setup();
        Assert.Equal(19, manifest.AllScopes.Count());
        Assert.Equal(5, manifest.ThemeIds.Count);
        Assert.Equal("dark-plus", manifest.DefaultThemeId);
    }

    [Fact]
    public void Every_grammar_loads_and_tokenizes()
    {
        (GrammarManifest manifest, Registry registry) = Setup();

        var broken = new List<string>();
        foreach (string scope in manifest.AllScopes)
        {
            IGrammar? grammar = registry.LoadGrammar(scope);
            if (grammar is null) { broken.Add($"{scope}: did not load"); continue; }

            // Compilation is lazy — the root rule is only built on first tokenize.
            try { grammar.TokenizeLine("x", null, Timeout); }
            catch (Exception ex) { broken.Add($"{scope}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Assert.Empty(broken);
    }

    [Fact]
    public void Every_theme_loads()
    {
        (GrammarManifest manifest, _) = Setup();
        var store = new GrammarStore(manifest);
        foreach (string id in manifest.ThemeIds) Assert.NotNull(store.GetTheme(id));
    }

    /// <summary>
    /// yaml.tmLanguage.json is a dispatcher that delegates to source.yaml.1.2 and
    /// source.yaml.embedded, which cross-reference the 1.0/1.1/1.3 variants. Load it
    /// alone and you get zero highlighting and no error, so assert on real tokens.
    /// </summary>
    [Fact]
    public void Yaml_resolves_its_six_file_dispatcher_chain()
    {
        (_, Registry registry) = Setup();
        IGrammar grammar = registry.LoadGrammar("source.yaml")!;

        ITokenizeLineResult result = grammar.TokenizeLine("key: \"value\"", null, Timeout);

        // meta.stream.yaml is defined in source.yaml.1.2, not in the dispatcher, so
        // seeing it is direct proof the delegation resolved.
        Assert.Contains(result.Tokens, t => t.Scopes.Contains("meta.stream.yaml"));
        Assert.Contains(result.Tokens, t => t.Scopes.Any(s => s.StartsWith("entity.name.tag")));
    }

    /// <summary>VS Code ships no TOML grammar; ours comes from taplo. Prove it loaded.</summary>
    [Fact]
    public void Toml_highlights()
    {
        (_, Registry registry) = Setup();
        IGrammar grammar = registry.LoadGrammar("source.toml")!;

        ITokenizeLineResult result = grammar.TokenizeLine("[package]", null, Timeout);

        Assert.Contains(result.Tokens, t => t.Scopes.Contains("support.type.property-name.table.toml"));
    }

    /// <summary>
    /// XML's JSP comment rule has "end" and "name" misplaced inside its captures map
    /// and a sibling rule with a begin and no end. Both throw out of TextMateSharp's
    /// rule compiler; RawGrammarFixup repairs them. Markdown depends on this too —
    /// compiling it resolves its fenced-code include of text.xml.
    /// </summary>
    [Theory]
    [InlineData("text.xml", "<!-- a comment -->")]
    [InlineData("text.html.markdown", "# Heading")]
    public void Grammars_with_upstream_malformations_still_tokenize(string scope, string line)
    {
        (_, Registry registry) = Setup();
        IGrammar grammar = registry.LoadGrammar(scope)!;

        ITokenizeLineResult result = grammar.TokenizeLine(line, null, Timeout);

        Assert.NotEmpty(result.Tokens);
    }

    [Fact]
    public void An_unknown_scope_is_null_rather_than_an_exception()
    {
        (GrammarManifest manifest, _) = Setup();
        Assert.Null(new GrammarStore(manifest).GetGrammar("source.nonexistent"));
    }
}
