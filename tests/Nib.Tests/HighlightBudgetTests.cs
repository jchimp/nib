using System.Diagnostics;
using Nib.Highlight;
using Nib.Model;
using Xunit;
using Xunit.Abstractions;

namespace Nib.Tests;

/// <summary>
/// The ROADMAP's phase-5 performance acceptance, as tests rather than as a feeling.
///
/// Thresholds are set well above the measured numbers on purpose. These exist to
/// catch a regression that changes the *order* of the cost — a convergence check
/// that stops working, a cache thrown away on every edit — not to police a few
/// hundred microseconds on a busy machine.
/// </summary>
public class HighlightBudgetTests
{
    private const int Lines = 20_000;
    private const int Window = 50;

    private readonly ITestOutputHelper _out;

    public HighlightBudgetTests(ITestOutputHelper output) => _out = output;

    private static TextBuffer BigYaml()
    {
        var lines = new List<Line>(Lines);
        for (int i = 0; i < Lines; i++)
        {
            // Enough shape that the tokenizer has real work: nesting, quoting, lists.
            lines.Add((i % 4) switch
            {
                0 => new Line($"service{i}:", LineEnding.Lf),
                1 => new Line($"  image: \"registry.example.com/app:{i}\"", LineEnding.Lf),
                2 => new Line($"  replicas: {i % 7}", LineEnding.Lf),
                _ => new Line($"  # comment for service {i}", LineEnding.Lf),
            });
        }
        return new TextBuffer(lines, DocumentEncoding.Utf8NoBom, "big.yaml");
    }

    /// <summary>
    /// "Editing line 3 of a 20k-line file re-tokenizes in under 5 ms." The cost is
    /// bounded by the visible window plus the convergence walk, not by file size.
    /// </summary>
    [Fact]
    public void An_edit_in_a_20k_line_file_re_tokenizes_within_the_frame_budget()
    {
        TextBuffer buffer = BigYaml();
        var highlighter = (TextMateHighlighter)TextMateHighlighter.Create(buffer);
        highlighter.TokenizeWindow(0, Window);

        // Warm the walk the way a few frames of scrolling would.
        for (int i = 0; i < 20; i++) highlighter.TokenizeWindow(0, Window);

        int before = highlighter.LinesTokenized;
        var clock = Stopwatch.StartNew();
        buffer.InsertText(3, 0, "x");
        highlighter.TokenizeWindow(0, Window);
        clock.Stop();

        int lines = highlighter.LinesTokenized - before;
        _out.WriteLine($"edit re-tokenized {lines} lines in {clock.Elapsed.TotalMilliseconds:N3} ms");

        // Most of those lines are the lookahead colouring rows below the window, not
        // the repair — HighlightCacheTests is where convergence itself is asserted.
        // What matters here is that the frame stays inside its budget.
        Assert.True(clock.Elapsed.TotalMilliseconds < 5, $"took {clock.Elapsed.TotalMilliseconds:N3} ms");
    }

    /// <summary>
    /// "Startup still under 100 ms to first paint." This measures the editor's share
    /// of that: load the file, build the highlighter, and colour the first screen.
    /// Process and runtime startup are measured separately by launching the exe.
    /// </summary>
    [Fact]
    public void Opening_a_20k_line_file_and_colouring_the_first_screen_is_quick()
    {
        var clock = Stopwatch.StartNew();
        TextBuffer buffer = BigYaml();
        IHighlighter highlighter = TextMateHighlighter.Create(buffer);
        highlighter.TokenizeWindow(0, Window);
        clock.Stop();

        _out.WriteLine($"open + first window: {clock.Elapsed.TotalMilliseconds:N1} ms");
        Assert.True(clock.Elapsed.TotalMilliseconds < 250, $"took {clock.Elapsed.TotalMilliseconds:N1} ms");
    }

    /// <summary>
    /// Scrolling far into a file it has not walked yet must not stall: the walk is
    /// sequential, so reaching row 19,000 means tokenizing everything above it. The
    /// bound that matters is that it completes, not that it is instant.
    /// </summary>
    [Fact]
    public void Jumping_to_the_end_of_a_20k_line_file_completes()
    {
        TextBuffer buffer = BigYaml();
        var highlighter = (TextMateHighlighter)TextMateHighlighter.Create(buffer);

        var clock = Stopwatch.StartNew();
        highlighter.TokenizeWindow(Lines - Window, Window);
        clock.Stop();

        _out.WriteLine($"jump to EOF: {clock.Elapsed.TotalMilliseconds:N0} ms for {highlighter.LinesTokenized} lines");
        Assert.NotNull(highlighter.Spans(Lines - 1));
        Assert.True(clock.Elapsed.TotalSeconds < 5, $"took {clock.Elapsed.TotalSeconds:N1} s");
    }
}
