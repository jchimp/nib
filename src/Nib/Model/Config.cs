namespace Nib.Model;

/// <summary>
/// The settings read from <c>%APPDATA%\nib\config.toml</c>: theme, tab width,
/// mouse, and the line-number gutter. Four keys, no schema, no writer — nib reads
/// this file and never creates it.
///
/// Lives in <c>Model/</c> because it is console-free by construction, which is what
/// lets the parser and its error cases be tested without a terminal. It is also why
/// <see cref="Load"/> takes the valid theme ids as an argument rather than asking
/// <c>Highlight/GrammarManifest</c> for them: <c>Model/</c> does not reach upward.
///
/// A missing file is the normal case, not an error. A malformed line is ignored,
/// leaves its key at the default, and is reported to the caller — a settings file
/// must never be able to stop the editor opening.
/// </summary>
public sealed record Config
{
    /// <summary>Anything outside this is a problem line, not a silent clamp — a
    /// <c>tab_width</c> of 0 is a typo, and honouring it would divide by zero in
    /// <see cref="TabStops.NextTabStop"/>.</summary>
    public const int MinTabWidth = 1;
    public const int MaxTabWidth = 16;

    private const string Section = "editor";

    /// <summary>Null means "no preference": the manifest's default theme wins.</summary>
    public string? Theme { get; init; }

    public int TabWidth { get; init; } = TabStops.DefaultTabWidth;

    /// <summary>
    /// Off by default, and deliberately so until the mouse handler lands: capturing
    /// the mouse costs the terminal its own ENABLE_QUICK_EDIT_MODE drag-select-and-copy,
    /// and the editor loop discards every mouse event it receives. That trade is worth
    /// making once clicks position the caret; it is a pure loss before then. Flip this
    /// back with phase 6's mouse work.
    /// </summary>
    public bool Mouse { get; init; }

    public bool LineNumbers { get; init; }

    public static Config Default { get; } = new();

    /// <summary>
    /// <c>%APPDATA%\nib\config.toml</c>. Neither the directory nor the file is
    /// created; if it is not there, <see cref="Load"/> returns the defaults.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "nib", "config.toml");

    /// <summary>
    /// Parse the file at <paramref name="path"/>. Every key that fails to parse or
    /// validate keeps its default and appends a <c>line N: ...</c> entry to
    /// <paramref name="problems"/>. Never throws for bad content; an unreadable file
    /// is reported the same way.
    /// </summary>
    public static Config Load(
        string path,
        IReadOnlyCollection<string> validThemeIds,
        out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        problems = found;

        string[] lines;
        try
        {
            if (!File.Exists(path)) return Default;
            // ReadAllLines strips a BOM and copes with either line ending, which a
            // config edited by whatever the user had to hand will have.
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            found.Add(ex.Message);
            return Default;
        }

        var config = Default;
        bool inSection = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line[0] == '[')
            {
                if (line.Length < 3 || line[^1] != ']')
                {
                    found.Add($"line {lineNo}: malformed table header");
                    continue;
                }
                string name = line[1..^1].Trim();
                inSection = string.Equals(name, Section, StringComparison.Ordinal);
                if (!inSection) found.Add($"line {lineNo}: unknown table \"{name}\"");
                continue;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                found.Add($"line {lineNo}: expected key = value");
                continue;
            }

            string key = line[..eq].Trim();
            string value = StripComment(line[(eq + 1)..].Trim());

            if (!inSection)
            {
                found.Add($"line {lineNo}: \"{key}\" is outside the [{Section}] table");
                continue;
            }
            if (!seen.Add(key))
            {
                found.Add($"line {lineNo}: duplicate key \"{key}\"");
                continue;
            }

            switch (key)
            {
                case "theme":
                    if (!TryString(value, out string theme))
                        found.Add($"line {lineNo}: theme must be a quoted string");
                    else if (!validThemeIds.Contains(theme, StringComparer.OrdinalIgnoreCase))
                        found.Add($"line {lineNo}: unknown theme \"{theme}\"");
                    else
                        config = config with { Theme = theme };
                    break;

                case "tab_width":
                    if (!int.TryParse(value, out int width))
                        found.Add($"line {lineNo}: tab_width must be a number");
                    else if (width < MinTabWidth || width > MaxTabWidth)
                        found.Add($"line {lineNo}: tab_width must be {MinTabWidth}-{MaxTabWidth}");
                    else
                        config = config with { TabWidth = width };
                    break;

                case "mouse":
                    if (!TryBool(value, out bool mouse))
                        found.Add($"line {lineNo}: mouse must be true or false");
                    else
                        config = config with { Mouse = mouse };
                    break;

                case "line_numbers":
                    if (!TryBool(value, out bool gutter))
                        found.Add($"line {lineNo}: line_numbers must be true or false");
                    else
                        config = config with { LineNumbers = gutter };
                    break;

                default:
                    found.Add($"line {lineNo}: unknown key \"{key}\"");
                    break;
            }
        }

        return config;
    }

    /// <summary>
    /// A one-line summary for the message row. The full list would run off the right
    /// edge, so past the first it says how many there are and lets the user open the
    /// file to see the rest.
    /// </summary>
    public static string DescribeProblems(IReadOnlyList<string> problems) => problems.Count switch
    {
        0 => "",
        1 => $"config.toml {problems[0]}",
        _ => $"config.toml: {problems.Count} problems (first, {problems[0]})",
    };

    // A trailing `# comment` is legal after a value. The scan has to be quote-aware
    // rather than "does the value start with a quote": a '#' inside a string is data,
    // but a quoted value can still be followed by a comment, and cutting at neither
    // or both is how this goes wrong.
    private static string StripComment(string value)
    {
        bool quoted = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '"') quoted = !quoted;
            else if (value[i] == '#' && !quoted) return value[..i].TrimEnd();
        }
        return value;
    }

    private static bool TryString(string value, out string result)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            result = value[1..^1];
            return true;
        }
        result = "";
        return false;
    }

    // TOML booleans are lowercase and unquoted; `yes` and `True` are not booleans.
    // Being strict here means a typo is reported rather than read as false.
    private static bool TryBool(string value, out bool result)
    {
        result = value == "true";
        return result || value == "false";
    }
}
