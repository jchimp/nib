using Nib;
using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The command line. Until config.toml there was nothing to layer under it, so the
/// arg loop had never been tested at all; the interesting property now is that an
/// unpassed flag comes back null rather than defaulted, because that is the only
/// thing distinguishing "the user asked for the default" from "the user said
/// nothing", and the second is the case where the config file gets to speak.
/// </summary>
public class StartupArgsTests
{
    [Fact]
    public void An_absent_flag_is_null_not_its_default()
    {
        ParsedArgs args = Program.Parse(new[] { "file.conf" });

        Assert.Null(args.Theme);
        Assert.Null(args.TabWidth);
        Assert.Null(args.Mouse);
        Assert.Null(args.LineNumbers);
    }

    [Fact]
    public void The_flags_that_existed_before_config_still_parse()
    {
        ParsedArgs args = Program.Parse(new[] { "--theme", "monokai", "-l", "+42", "notes.md" });

        Assert.Equal("monokai", args.Theme);
        Assert.True(args.LineNumbers);
        Assert.Equal(42, args.StartLine);
        Assert.Equal("notes.md", args.File);
    }

    [Fact]
    public void Plus_line_is_not_mistaken_for_the_filename()
    {
        ParsedArgs args = Program.Parse(new[] { "+7", "app.yaml" });

        Assert.Equal(7, args.StartLine);
        Assert.Equal("app.yaml", args.File);
        Assert.Equal(new[] { "app.yaml" }, args.Positional);
    }

    [Fact]
    public void The_new_settings_flags_parse_in_both_directions()
    {
        Assert.False(Program.Parse(new[] { "--no-mouse" }).Mouse);
        Assert.True(Program.Parse(new[] { "--mouse" }).Mouse);
        Assert.False(Program.Parse(new[] { "--no-line-numbers" }).LineNumbers);
        Assert.Equal(4, Program.Parse(new[] { "--tab-width", "4" }).TabWidth);
        Assert.True(Program.Parse(new[] { "--no-config" }).NoConfig);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("four")]
    public void An_out_of_range_tab_width_falls_through_to_the_config_and_default(string value)
    {
        // Null rather than clamped: the same rule as the file. A refused flag should
        // leave whatever config.toml says standing, not silently become 8.
        Assert.Null(Program.Parse(new[] { "--tab-width", value }).TabWidth);
    }

    [Fact]
    public void Soak_keeps_its_positional_directory_and_pass_count()
    {
        ParsedArgs args = Program.Parse(new[] { "--soak", "fixtures", "20" });

        Assert.True(args.Soak);
        Assert.Equal(new[] { "fixtures", "20" }, args.Positional);
    }

    [Fact]
    public void Help_and_version_are_flags_now_rather_than_early_returns()
    {
        Assert.True(Program.Parse(new[] { "--help" }).Help);
        Assert.True(Program.Parse(new[] { "-v" }).Version);
    }

    [Fact]
    public void Command_line_beats_config_beats_default()
    {
        // The merge Main performs, spelled out: it is three ?? operators and the
        // whole point of the nullable fields, so it is worth pinning somewhere.
        var config = new Config { TabWidth = 4, Mouse = false, LineNumbers = true };

        ParsedArgs bare = Program.Parse(Array.Empty<string>());
        Assert.Equal(4, bare.TabWidth ?? config.TabWidth);
        Assert.False(bare.Mouse ?? config.Mouse);

        ParsedArgs flagged = Program.Parse(new[] { "--tab-width", "2", "--mouse" });
        Assert.Equal(2, flagged.TabWidth ?? config.TabWidth);
        Assert.True(flagged.Mouse ?? config.Mouse);

        Assert.Equal(TabStops.DefaultTabWidth, bare.TabWidth ?? Config.Default.TabWidth);
    }
}
