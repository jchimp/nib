using Nib.Highlight;
using Nib.Terminal;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// Scope to colour. The exact Dark+ values are asserted deliberately: a theme that
/// loads but resolves everything to the terminal default looks, from the outside,
/// exactly like a theme that works.
/// </summary>
public class ThemeMapTests
{
    private static ThemeMap Map(string themeId)
    {
        GrammarManifest manifest = GrammarManifest.Load();
        var store = new GrammarStore(manifest);
        var registry = new Registry(store);
        registry.SetTheme(store.GetTheme(themeId)!);
        return new ThemeMap(registry.GetTheme(), manifest.ThemeLabels[themeId]);
    }

    [Theory]
    [InlineData("keyword.control", 0xC5, 0x86, 0xC0)]
    [InlineData("string.quoted.double", 0xCE, 0x91, 0x78)]
    [InlineData("comment.line.number-sign", 0x6A, 0x99, 0x55)]
    [InlineData("constant.numeric", 0xB5, 0xCE, 0xA8)]
    public void Dark_plus_resolves_the_common_scopes(string scope, byte r, byte g, byte b)
    {
        Color actual = Map("dark-plus").Foreground(["source.python", scope]);
        Assert.Equal(Color.Rgb(r, g, b), actual);
    }

    [Fact]
    public void An_unmatched_scope_falls_back_to_the_terminal_default()
    {
        Color actual = Map("dark-plus").Foreground(["source.python", "no.such.scope.at.all"]);
        Assert.True(actual.IsDefault);
    }

    [Fact]
    public void An_empty_scope_list_is_the_default() =>
        Assert.True(Map("dark-plus").Foreground([]).IsDefault);

    [Fact]
    public void Repeated_lookups_agree_with_the_first_one()
    {
        ThemeMap map = Map("dark-plus");
        string[] scopes = ["source.python", "keyword.control"];
        Assert.Equal(map.Foreground(scopes), map.Foreground(scopes));
    }

    /// <summary>Different themes must actually differ, or Alt+T does nothing visible.</summary>
    [Fact]
    public void Themes_disagree_about_keywords()
    {
        string[] scopes = ["source.python", "keyword.control"];
        Assert.NotEqual(Map("dark-plus").Foreground(scopes), Map("monokai").Foreground(scopes));
    }

    /// <summary>
    /// Dark+ and Light+ declare no editor.selectionBackground — VS Code supplies one
    /// from its own built-in defaults, which are not in the theme file and which we
    /// do not vendor. Null is therefore the correct answer for them, and the view
    /// falls back to its own colour. Themes that do declare one must expose it.
    /// </summary>
    [Fact]
    public void Selection_background_is_the_theme_s_when_it_declares_one()
    {
        Assert.Null(Map("dark-plus").SelectionBackground);
        Assert.Null(Map("light-plus").SelectionBackground);
        Assert.Equal(Color.Rgb(0x27, 0x46, 0x42), Map("solarized-dark").SelectionBackground);
    }

    /// <summary>Monokai's selection colour carries an alpha byte; a console cell has no blending.</summary>
    [Fact]
    public void An_eight_digit_hex_drops_its_alpha() =>
        Assert.Equal(Color.Rgb(0x87, 0x8B, 0x91), Map("monokai").SelectionBackground);
}
