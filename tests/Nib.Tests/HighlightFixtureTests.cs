using Nib.Highlight;
using Nib.Model;
using Nib.Terminal;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// Every language we ship colours a real file of that language.
///
/// This is the ROADMAP's "all languages highlight correctly on a representative
/// sample file each", made automatic. Eyeballing thirteen files in a terminal
/// catches a language going dark once; this catches it on every run, and it is the
/// test that would have failed loudly when XML and Markdown were throwing out of
/// TextMateSharp's rule compiler.
///
/// The assertions are deliberately coarse — several distinct colours, over several
/// rows. Pinning exact colours per token would break on every upstream grammar
/// tweak while proving nothing more than "it is coloured".
/// </summary>
public class HighlightFixtureTests
{
    private static string FixtureDir()
    {
        // Walk up from the test binary to the repo root. The fixtures are checked
        // in, not copied to the output directory — they are also opened by hand
        // during the interactive acceptance run.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "fixtures", "highlight");
    }

    public static TheoryData<string> Fixtures =>
    [
        "sample.ini", "sample.toml", "sample.yaml", "sample.json", "sample.xml",
        "sample.sql", "sample.py", "sample.js", "sample.rs", "sample.go",
        "sample.sh", "sample.ps1", "sample.bat", "sample.md",
    ];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Each_language_produces_colour(string fixture)
    {
        string path = Path.Combine(FixtureDir(), fixture);
        Assert.True(File.Exists(path), $"missing fixture {path}");

        TextBuffer buffer = FileIo.Load(path);
        IHighlighter highlighter = TextMateHighlighter.Create(buffer);
        Assert.IsType<TextMateHighlighter>(highlighter);

        highlighter.TokenizeWindow(0, buffer.LineCount);

        var colours = new HashSet<Color>();
        int colouredRows = 0;
        for (int row = 0; row < buffer.LineCount; row++)
        {
            IReadOnlyList<StyledSpan>? spans = highlighter.Spans(row);
            if (spans is null || spans.Count == 0) continue;
            colouredRows++;
            foreach (StyledSpan span in spans) colours.Add(span.Fg);
        }

        // Two colours, not three: source.ini is a four-rule grammar and honestly has
        // no more to say about a config file. Spans that resolve to the terminal
        // default are never emitted, so any colour here is real highlighting.
        Assert.True(colouredRows >= 3, $"{fixture}: only {colouredRows} rows coloured");
        Assert.True(colours.Count >= 2, $"{fixture}: only {colours.Count} distinct colours");
    }

    /// <summary>
    /// Markdown references 61 external scopes for fenced code blocks. The languages
    /// we ship highlight inside fences; the rest degrade to plain text. Prove the
    /// shipped side works rather than assuming it.
    /// </summary>
    [Fact]
    public void Markdown_highlights_inside_a_fenced_python_block()
    {
        TextBuffer buffer = FileIo.Load(Path.Combine(FixtureDir(), "sample.md"));
        IHighlighter highlighter = TextMateHighlighter.Create(buffer);
        highlighter.TokenizeWindow(0, buffer.LineCount);

        int fence = -1;
        for (int row = 0; row < buffer.LineCount; row++)
        {
            if (buffer.GetLine(row).StartsWith("def hello", StringComparison.Ordinal)) { fence = row; break; }
        }

        Assert.True(fence >= 0, "fixture no longer contains the fenced python block");
        Assert.NotEmpty(highlighter.Spans(fence)!);
    }

    [Fact]
    public void An_unknown_extension_opens_uncoloured_without_error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nib-fixture-{Guid.NewGuid():N}.xyz");
        File.WriteAllText(path, "just some text\nwith two lines\n");
        try
        {
            TextBuffer buffer = FileIo.Load(path);
            IHighlighter highlighter = TextMateHighlighter.Create(buffer);

            Assert.IsType<NullHighlighter>(highlighter);
            highlighter.TokenizeWindow(0, buffer.LineCount); // must be a no-op, not a throw
            Assert.Null(highlighter.Spans(0));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
