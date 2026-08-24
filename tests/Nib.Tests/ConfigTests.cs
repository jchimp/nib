using System.Text;
using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The config.toml parser and its failure cases. The rule the tests exist to hold:
/// a bad line costs its own key and nothing else. Every one of these files opens
/// the editor — none of them is allowed to be fatal, because a settings file that
/// can lock you out of your editor is worse than no settings file.
///
/// Console-free, like everything in <c>Model/</c>.
/// </summary>
public class ConfigTests
{
    private static readonly string[] Themes =
        { "dark-plus", "light-plus", "monokai", "solarized-dark", "high-contrast" };

    private static Config Parse(string text, out IReadOnlyList<string> problems) =>
        Parse(Encoding.UTF8.GetBytes(text), out problems);

    private static Config Parse(byte[] bytes, out IReadOnlyList<string> problems)
    {
        string path = Path.Combine(Path.GetTempPath(), $"nib-cfg-{Guid.NewGuid():N}.toml");
        try
        {
            File.WriteAllBytes(path, bytes);
            return Config.Load(path, Themes, out problems);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_is_the_normal_case_not_an_error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nib-cfg-{Guid.NewGuid():N}.toml");
        Config config = Config.Load(path, Themes, out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.Equal(Config.Default, config);
    }

    [Fact]
    public void Defaults_match_the_behaviour_before_there_was_a_config_file()
    {
        Assert.Null(Config.Default.Theme);
        Assert.Equal(TabStops.DefaultTabWidth, Config.Default.TabWidth);
        Assert.True(Config.Default.Mouse);
        Assert.False(Config.Default.LineNumbers);
    }

    [Fact]
    public void All_four_keys_load()
    {
        Config config = Parse("""
            [editor]
            theme = "monokai"
            tab_width = 4
            mouse = false
            line_numbers = true
            """, out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.Equal("monokai", config.Theme);
        Assert.Equal(4, config.TabWidth);
        Assert.False(config.Mouse);
        Assert.True(config.LineNumbers);
    }

    [Fact]
    public void Comments_blank_lines_and_stray_whitespace_are_tolerated()
    {
        Config config = Parse("""
            # nib settings

              [editor]
            theme   =   "monokai"     # the one with the pink
            tab_width = 2

            """, out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.Equal("monokai", config.Theme);
        Assert.Equal(2, config.TabWidth);
    }

    [Theory]
    [InlineData("tabwidth = 4", "unknown key")]
    [InlineData("theme = \"monokia\"", "unknown theme")]
    [InlineData("theme = monokai", "quoted string")]
    [InlineData("tab_width = 0", "tab_width must be")]
    [InlineData("tab_width = 99", "tab_width must be")]
    [InlineData("tab_width = \"eight\"", "tab_width must be a number")]
    [InlineData("mouse = yes", "mouse must be")]
    [InlineData("mouse = True", "mouse must be")]
    [InlineData("line_numbers = 1", "line_numbers must be")]
    [InlineData("nonsense", "expected key = value")]
    public void A_bad_line_costs_its_own_key_and_nothing_else(string bad, string expected)
    {
        // Every key the bad line does not name is set to a non-default and bracketed
        // around it, so a parser that gives up at the first problem — or that lets one
        // bad value poison a later good one — fails here rather than quietly dropping
        // the rest of the file.
        var good = new List<string>();
        if (!Names(bad, "theme")) good.Add("theme = \"solarized-dark\"");
        if (!Names(bad, "tab_width")) good.Add("tab_width = 4");
        if (!Names(bad, "mouse")) good.Add("mouse = false");
        if (!Names(bad, "line_numbers")) good.Add("line_numbers = true");

        string text = string.Join('\n',
            new[] { "[editor]", good[0], bad }.Concat(good.Skip(1))) + "\n";
        Config config = Parse(text, out IReadOnlyList<string> problems);

        string problem = Assert.Single(problems);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.Contains("line 3", problem, StringComparison.Ordinal);

        if (!Names(bad, "theme")) Assert.Equal("solarized-dark", config.Theme);
        if (!Names(bad, "tab_width")) Assert.Equal(4, config.TabWidth);
        if (!Names(bad, "mouse")) Assert.False(config.Mouse);
        if (!Names(bad, "line_numbers")) Assert.True(config.LineNumbers);
    }

    private static bool Names(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal);

    [Fact]
    public void An_unknown_theme_leaves_the_manifest_default_in_charge()
    {
        Config config = Parse("[editor]\ntheme = \"dracula\"\n", out _);

        // Null, not the string: Program hands this straight to the highlighter, and
        // a bad id kept here would be resolved as "no match" one layer further down
        // where there is nobody left to report it.
        Assert.Null(config.Theme);
    }

    [Fact]
    public void Theme_ids_match_case_insensitively_as_the_theme_flag_does()
    {
        Config config = Parse("[editor]\ntheme = \"Dark-Plus\"\n", out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.Equal("Dark-Plus", config.Theme);
    }

    [Fact]
    public void Keys_outside_the_editor_table_are_reported_not_silently_applied()
    {
        Config config = Parse("tab_width = 4\n", out IReadOnlyList<string> problems);

        Assert.Contains("outside the [editor] table", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Equal(TabStops.DefaultTabWidth, config.TabWidth);
    }

    [Fact]
    public void An_unknown_table_is_reported_and_its_keys_are_not_read()
    {
        Config config = Parse("[colours]\ntab_width = 4\n", out IReadOnlyList<string> problems);

        Assert.Equal(2, problems.Count);
        Assert.Contains("unknown table", problems[0], StringComparison.Ordinal);
        Assert.Equal(TabStops.DefaultTabWidth, config.TabWidth);
    }

    [Fact]
    public void A_duplicate_key_keeps_the_first_and_says_so()
    {
        Config config = Parse("[editor]\ntab_width = 4\ntab_width = 2\n",
            out IReadOnlyList<string> problems);

        Assert.Contains("duplicate key", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Equal(4, config.TabWidth);
    }

    [Fact]
    public void CRLF_endings_and_a_UTF8_BOM_both_parse()
    {
        var bytes = new List<byte>(Encoding.UTF8.GetPreamble());
        bytes.AddRange(Encoding.UTF8.GetBytes("[editor]\r\ntab_width = 3\r\nmouse = false\r\n"));

        Config config = Parse(bytes.ToArray(), out IReadOnlyList<string> problems);

        Assert.Empty(problems);
        Assert.Equal(3, config.TabWidth);
        Assert.False(config.Mouse);
    }

    [Fact]
    public void A_hash_inside_a_quoted_value_is_data_not_a_comment()
    {
        // Not reachable with today's theme ids, but the stripper runs before
        // validation and would otherwise mangle any future value containing one.
        Config config = Parse("[editor]\ntheme = \"mono#kai\"\n", out IReadOnlyList<string> problems);

        Assert.Contains("unknown theme \"mono#kai\"", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Null(config.Theme);
    }

    [Fact]
    public void Problem_summaries_stay_short_enough_for_the_message_row()
    {
        Assert.Equal("", Config.DescribeProblems(Array.Empty<string>()));

        Config _ = Parse("[editor]\ntabwidth = 4\nmouse = yes\ntheme = 1\n",
            out IReadOnlyList<string> problems);

        string summary = Config.DescribeProblems(problems);
        Assert.Contains("3 problems", summary, StringComparison.Ordinal);
        Assert.True(summary.Length <= 78, $"message row is 80 columns: {summary}");
    }
}
