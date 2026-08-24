using System.Reflection;
using Nib.Commands;
using Nib.Highlight;
using Nib.Model;
using Nib.Terminal;
using Nib.Ui;

namespace Nib;

/// <summary>
/// Entry point and the editor loop. <c>nib &lt;file&gt;</c> opens a file for editing
/// — or, if it does not exist yet, an empty buffer already named for it, so a save
/// needs no prompt. <c>+LINE</c> opens at a line; <c>nib --probe</c> runs the
/// phase-1 terminal probe. Loading and saving go through <see cref="FileIo"/>, so
/// bytes, encoding, and line endings are preserved.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        ParsedArgs cli = Parse(args);
        if (cli.Help) { PrintHelp(); return 0; }
        if (cli.Version) { PrintVersion(); return 0; }

        // Diagnostic path: no console acquisition, no config, ordinary stdout. See Soak.
        if (cli.Soak)
        {
            string? dir = cli.Positional.Count > 0 ? cli.Positional[0] : null;
            int passes = cli.Positional.Count > 1 && int.TryParse(cli.Positional[1], out int p) ? p : 3;
            return Highlight.Soak.Run(dir, passes);
        }

        // Command line beats config file beats built-in default. Every settings
        // field on ParsedArgs is nullable for exactly this: "--no-mouse was not
        // passed" and "mouse was set to the default" have to be distinguishable, or
        // a config value could never win over a flag that was never typed.
        Config config = Config.Default;
        string? configProblem = null;
        if (!cli.NoConfig)
        {
            config = Config.Load(
                Config.DefaultPath,
                GrammarManifest.Load().ThemeIds,
                out IReadOnlyList<string> problems);
            if (problems.Count > 0) configProblem = Config.DescribeProblems(problems);
        }

        string? theme = cli.Theme ?? config.Theme;
        int tabWidth = cli.TabWidth ?? config.TabWidth;
        bool mouse = cli.Mouse ?? config.Mouse;
        bool lineNumbers = cli.LineNumbers ?? config.LineNumbers;
        bool probe = cli.Probe;
        string? file = cli.File;

        // Load before touching the console: a bad path should print to a normal
        // shell, not from inside the alternate screen.
        TextBuffer? buffer = null;
        string? openingMessage = null;
        if (!probe)
        {
            try
            {
                if (file is null)
                {
                    buffer = TextBuffer.Empty();
                }
                else if (File.Exists(file))
                {
                    buffer = FileIo.Load(file);
                }
                else if (Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(file)) ?? "."))
                {
                    // `nib newfile.conf` starts an empty buffer already bound to that
                    // path, so Ctrl+S writes it without a prompt — nano's behaviour, and
                    // the reason FileIo.Save has a File.Move branch. The directory check
                    // matters: without it a typo'd path opens a buffer that can never be
                    // saved, and the user only finds out after typing into it.
                    buffer = TextBuffer.Empty(file);
                    openingMessage = "New File";
                }
                else
                {
                    Console.Error.WriteLine($"nib: {file}: directory does not exist");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"nib: {file}: {ex.Message}");
                return 1;
            }
        }

        // A settings error the user can act on outranks "New File", which the title
        // row already says.
        if (configProblem is not null) openingMessage = configProblem;

        ConsoleHost host;
        try
        {
            host = ConsoleHost.Acquire(enableMouse: mouse);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nib: {ex.Message}");
            return 1;
        }

        try
        {
            if (probe) Probe.Run(host);
            else new Editor(
                host, buffer!, theme, openingMessage, cli.StartLine, lineNumbers, tabWidth,
                configProblem is null ? MessageKind.Info : MessageKind.Error).Run();
            return 0;
        }
        finally
        {
            // Primary restore path; ConsoleHost also hooks ProcessExit,
            // UnhandledException and the console control handler.
            host.Dispose();
        }
    }

    /// <summary>
    /// The command line, with the four config-backed settings left null when their
    /// flag was not passed so <see cref="Main"/> can layer the config file under
    /// them. Unrecognised <c>-flags</c> are ignored, as they always have been.
    /// </summary>
    internal static ParsedArgs Parse(string[] args)
    {
        var result = new ParsedArgs();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--probe": result.Probe = true; break;
                case "--soak": result.Soak = true; break;
                case "--theme": result.Theme = i + 1 < args.Length ? args[++i] : null; break;
                case "--tab-width":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int w)
                        && w >= Config.MinTabWidth && w <= Config.MaxTabWidth)
                        result.TabWidth = w;
                    break;
                case "--mouse": result.Mouse = true; break;
                case "--no-mouse": result.Mouse = false; break;
                case "--no-config": result.NoConfig = true; break;
                case "--line-numbers" or "-l": result.LineNumbers = true; break;
                case "--no-line-numbers": result.LineNumbers = false; break;
                case "--help" or "-h": result.Help = true; break;
                case "--version" or "-v": result.Version = true; break;
                default:
                    // `+123` opens at a line, as nano does. It does not start with '-',
                    // so without this it would be taken for the filename.
                    if (a.Length > 1 && a[0] == '+' && int.TryParse(a[1..], out int n) && n > 0)
                        result.StartLine = n;
                    else if (!a.StartsWith('-'))
                    {
                        result.Positional.Add(a);
                        result.File ??= a;
                    }
                    break;
            }
        }

        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("nib — a console text editor");
        Console.WriteLine();
        Console.WriteLine("usage: nib [options] [+LINE] [file]");
        Console.WriteLine();
        Console.WriteLine("  --theme <id>   dark-plus | light-plus | monokai | solarized-dark | high-contrast");
        Console.WriteLine("  -l, --line-numbers, --no-line-numbers");
        Console.WriteLine("                 show the line-number gutter (Alt+N toggles it)");
        Console.WriteLine("  --tab-width <n>");
        Console.WriteLine($"                 tab stop every n columns, {Config.MinTabWidth}-{Config.MaxTabWidth} (default {TabStops.DefaultTabWidth})");
        Console.WriteLine("  --mouse, --no-mouse");
        Console.WriteLine("                 capture the mouse; off leaves the terminal its own");
        Console.WriteLine("                 drag-select and copy");
        Console.WriteLine("  --no-config    ignore config.toml");
        Console.WriteLine("  --probe        run the phase-1 terminal probe");
        Console.WriteLine("  --soak [dir] [passes]");
        Console.WriteLine("                 tokenizer soak test; not part of the editor");
        Console.WriteLine("  -h, --help");
        Console.WriteLine("  -v, --version");
        Console.WriteLine();
        Console.WriteLine("A file that does not exist opens as a new, empty buffer under that name.");
        Console.WriteLine();
        Console.WriteLine($"config: {Config.DefaultPath}");
        Console.WriteLine("        [editor] theme, tab_width, mouse, line_numbers.");
        Console.WriteLine("        Optional; a flag above beats it. nib never writes this file.");
        Console.WriteLine();
        Console.WriteLine("keys: arrows / Home / End / PgUp / PgDn move; Ctrl+arrows by word;");
        Console.WriteLine("      Shift+move selects; Esc clears the selection; Ctrl+A select all;");
        Console.WriteLine("      Ctrl+C/X/V copy/cut/paste; Ctrl+K/U cut/paste line; Ctrl+Z/Y undo/redo;");
        Console.WriteLine("      Ctrl+S or Ctrl+O save; Ctrl+Q quit; Ctrl+X cut, or quit with no selection;");
        Console.WriteLine("      Ctrl+G go to line; Ctrl+H full keymap;");
        Console.WriteLine("      Ctrl+F find, F3 / Shift+F3 next / previous, Ctrl+R replace;");
        Console.WriteLine("      Alt+C case, Alt+W whole word, both inside the search prompt;");
        Console.WriteLine("      Ctrl+W line / word / character count, for the selection or the file;");
        Console.WriteLine("      Alt+T cycle theme; Alt+N toggle line numbers.");
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"nib {Version}");
    }

    /// <summary>
    /// The assembly's informational version, which is what &lt;Version&gt; in the
    /// csproj (and <c>-p:Version=</c> on the release publish) actually stamps.
    /// AssemblyVersion is truncated to four numeric parts, so it cannot carry a
    /// prerelease suffix; the informational one can, and the SDK appends
    /// <c>+&lt;commit-sha&gt;</c> to it when SourceLink is active — trim that, the
    /// zip's VERSION.txt records the commit.
    /// </summary>
    internal static string Version
    {
        get
        {
            string? v = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) return "0.0.0";
            int plus = v.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? v : v[..plus];
        }
    }
}

/// <summary>
/// What <see cref="Program.Parse"/> found on the command line. The four settings
/// that <c>config.toml</c> also carries are nullable: null means "the flag was not
/// passed", which is what lets the config file supply a value without a flag that
/// defaults to the same thing overriding it.
/// </summary>
internal sealed class ParsedArgs
{
    public bool Probe { get; set; }
    public bool Soak { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }
    public bool NoConfig { get; set; }

    public string? File { get; set; }
    public int StartLine { get; set; } // 0 = unset; nano's +LINE
    public List<string> Positional { get; } = new();

    public string? Theme { get; set; }
    public int? TabWidth { get; set; }
    public bool? Mouse { get; set; }
    public bool? LineNumbers { get; set; }
}

/// <summary>
/// Owns the interactive editing session: the render/input loop, the modal prompt
/// and confirm lines, and save/quit. Everything editing-related lives in
/// <see cref="TextBuffer"/>/<see cref="Cursor"/>; this class only wires them to the
/// console.
/// </summary>
internal sealed class Editor
{
    private readonly ConsoleHost _host;
    private readonly TextBuffer _buffer;
    private readonly Screen _screen;
    private readonly Viewport _viewport;
    private readonly Cursor _cursor;
    private readonly EditorView _view;
    private readonly EditorCommands _commands;
    private readonly InputReader _reader;
    private readonly IHighlighter _highlighter;
    private readonly List<InputEvent> _batch = new(256);
    // Modal prompts (Save-as, quit confirm) read input while the main loop is
    // still enumerating _batch. They must not touch it, or the outer foreach
    // throws "Collection was modified". Separate buffer, never nested modally.
    private readonly List<InputEvent> _modalBatch = new(64);

    public Editor(
        ConsoleHost host,
        TextBuffer buffer,
        string? themeId,
        string? openingMessage = null,
        int startLine = 0,
        bool lineNumbers = false,
        int tabWidth = TabStops.DefaultTabWidth,
        MessageKind openingKind = MessageKind.Info)
    {
        _host = host;
        _buffer = buffer;
        (int w, int h) = host.GetWindowSize();
        _screen = new Screen(w, h);
        _viewport = new Viewport(tabWidth);
        _cursor = new Cursor(buffer, _viewport.TabWidth);
        _view = new EditorView(_screen, _viewport, buffer, _cursor);
        _commands = new EditorCommands(buffer, _cursor, new SystemClipboard());
        _view.Selection = _commands.Selection; // the view paints the live selection
        _reader = new InputReader(host);

        // Falls back to NullHighlighter for an unknown file type or a bad resource:
        // no colour, no error, no reason for the user to care.
        _highlighter = TextMateHighlighter.Create(buffer, themeId);
        _view.Highlighter = _highlighter;
        _view.ShowLineNumbers = lineNumbers;

        // `+LINE` past the end of the file lands on the last line rather than
        // refusing — the prompt reports out-of-range, but a command line is not a
        // conversation and silently doing the nearest sensible thing is nano's habit.
        if (startLine > 0) _commands.TryGoToLine(Math.Min(startLine, buffer.LineCount));

        // Cleared by the next keystroke, which is the right lifetime for "New File"
        // and for a config complaint the user will go and fix in another session.
        _view.SetMessage(openingMessage ?? "", openingKind);
    }

    public void Run()
    {
        _host.ShowCursor();
        Draw();

        while (true)
        {
            _batch.Clear();
            if (_reader.ReadBatch(_batch) == 0) continue;

            _view.Message = ""; // stale feedback clears on the next keystroke
            int pageRows = Math.Max(1, _view.TextRows - 1);

            foreach (InputEvent ev in _batch)
            {
                if (ev.Kind == InputEventKind.Resize) { _screen.Resize(ev.Width, ev.Height); continue; }
                if (ev.Kind == InputEventKind.Mouse) continue; // phase 6

                // A bug in a command must not cost the user their buffer. Before
                // this, any exception escaping a keystroke unwound out of Main, and
                // because ConsoleHost restores the terminal on every exit path it
                // looked like a clean quit rather than a crash — the editor simply
                // vanished with everything unsaved. Report it on the message row and
                // keep going: whatever is broken, the buffer is still in memory and
                // the user can still save it.
                try
                {
                    switch (Keymap.Handle(ev, _commands, pageRows))
                    {
                        case EditorAction.Save: DoSave(); break;
                        case EditorAction.Quit: if (TryQuit()) return; break;
                        // A full screen rather than a one-line hint. The hint could
                        // not hold the keymap once search arrived, and the last time
                        // it was asked to, half of it was clipped off the right edge.
                        case EditorAction.Help: ShowHelp(); break;
                        case EditorAction.GoToLine: DoGoToLine(); break;
                        case EditorAction.CycleTheme: CycleTheme(); break;
                        case EditorAction.ToggleLineNumbers: ToggleLineNumbers(); break;
                        case EditorAction.Find: DoFind(); break;
                        case EditorAction.Replace: DoReplace(); break;
                        case EditorAction.FindNext: DoFindAgain(backwards: false); break;
                        case EditorAction.FindPrevious: DoFindAgain(backwards: true); break;
                        case EditorAction.Stats: ShowStats(); break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Recover(ex);
                }
            }

            Draw();
        }
    }

    // A command threw. Put the cursor somewhere legal — MoveTo clamps, and a cursor
    // left pointing outside the buffer would throw again on the very next frame and
    // turn one bad keystroke into an unusable editor — then say what happened.
    // Deliberately not caught around Draw(): if painting is what is broken, there is
    // nowhere left to report it and looping on it would only hang.
    private void Recover(Exception ex)
    {
        _cursor.MoveTo(_cursor.Row, _cursor.Col);
        _view.SetMessage(
            $"Internal error ({ex.GetType().Name}: {ex.Message}) — your text is intact, ^S to save",
            MessageKind.Error);
    }

    // Alt+N. Says which way it went, because on a short file the gutter is narrow
    // enough that the change is easy to miss.
    private void ToggleLineNumbers()
    {
        _view.ShowLineNumbers = !_view.ShowLineNumbers;
        _view.Message = _view.ShowLineNumbers ? "Line numbers on" : "Line numbers off";
    }

    // Alt+T. A theme the grammar-less path can't honour just says so rather than
    // silently doing nothing.
    private void CycleTheme()
    {
        _view.Message = _highlighter is TextMateHighlighter tm
            ? $"Theme: {tm.CycleTheme()}"
            : "No highlighting for this file type";
    }

    // One frame: clamp/scroll to the caret, bring the visible rows' tokens up to
    // date, paint, then place the hardware cursor where the view computed it, and
    // flush once. Tokenizing here rather than inside the painter is deliberate —
    // the render path itself never runs a regex.
    private void Draw()
    {
        _viewport.ClampVertical(_buffer.LineCount);
        // TextColumns, not the screen width: the line-number gutter takes columns off
        // the left, and scrolling calibrated to the full width slides the caret under
        // it on a long line and strands the rightmost columns.
        _viewport.EnsureVisible(_cursor.Row, _cursor.DisplayColumn, _view.TextRows, _view.TextColumns);
        _highlighter.Pump();
        _highlighter.TokenizeWindow(_viewport.FirstLine, _view.TextRows);
        _view.Render();
        _screen.Render(_host.Out);
        _host.Out.MoveTo(_view.CursorY + 1, _view.CursorX + 1);
        _host.Out.Flush();
    }

    // Save to the buffer's path, prompting for one if it is new. Returns whether a
    // write actually happened (the quit-then-save path needs to know).
    private bool DoSave()
    {
        string? path = _buffer.Path;
        if (path is null)
        {
            path = RunPrompt("Save as: ");
            if (string.IsNullOrEmpty(path)) { _view.Message = "Cancelled"; return false; }
        }

        try
        {
            FileIo.Save(_buffer, path);
            _view.Message = $"Wrote {LinesWritten()} lines";
            return true;
        }
        catch (Exception ex)
        {
            _view.SetMessage($"Error: {ex.Message}", MessageKind.Error);
            return false;
        }
    }

    // Ctrl+G. Reuses the save-as prompt rather than growing a second modal editor.
    // An unparseable or out-of-range line says so and leaves the caret alone; Esc
    // cancels without a message, because the user already knows they cancelled.
    private void DoGoToLine()
    {
        string? entered = RunPrompt("Go to line: ");
        if (entered is null) return;

        entered = entered.Trim();
        if (entered.Length == 0) return;

        if (!int.TryParse(entered, out int line))
        {
            _view.SetMessage($"Not a line number: {entered}", MessageKind.Error);
            return;
        }

        if (!_commands.TryGoToLine(line))
            _view.SetMessage($"No line {line} (buffer has {_buffer.LineCount})", MessageKind.Error);
    }

    // ---- search and replace -------------------------------------------------

    // Ctrl+F. The term and the toggles are remembered, so the prompt opens pre-filled
    // with the last search and F3 can repeat it without one.
    private void DoFind()
    {
        if (PromptForQuery("Search") is not { } query) return;
        Report(_commands.Find(query, backwards: false), query.Text);
    }

    // F3 / Shift+F3. No prompt at all — direction is the key, not a mode, so there is
    // never a toggle to be caught on the wrong side of.
    private void DoFindAgain(bool backwards)
        => Report(_commands.FindAgain(backwards), _commands.Search.Text);

    // Ctrl+R. Walks the file offering each hit, and hands the rest to a single
    // undo-step ReplaceAll if the user answers A.
    //
    // The pass starts at the top rather than at the caret. nano replaces from the
    // cursor, but "Replaced 3" on a file containing 11 hits — because the other 8
    // were above where you happened to be sitting — is a bug report waiting to
    // happen, and starting at the top also gives the walk a natural end.
    private void DoReplace()
    {
        if (PromptForQuery("Replace") is not { } query) return;

        string? with = RunPrompt("Replace with: ", _commands.Search.Replacement);
        if (with is null) return;
        _commands.Search.Replacement = with;

        // Where the user was, in case the pass changes nothing. Walking from the top
        // means even a cancelled replace has already moved them, and being dumped at
        // line 1 for answering Esc is exactly the "lost my place" that search itself
        // is careful not to do.
        int homeRow = _commands.Cursor.Row;
        int homeCol = _commands.Cursor.Col;

        _commands.Move(_commands.Cursor.DocumentStart, extend: false);

        int replaced = 0;
        bool cancelled = false;
        while (true)
        {
            // Only Found continues: FoundWrapped means the scan has come back round
            // to the top and every hit has already been offered once.
            if (_commands.FindAgain(backwards: false) != SearchOutcome.Found) break;
            if (_commands.LastMatch is not { } match) break;

            ReplaceAnswer answer = ConfirmReplace();
            if (answer == ReplaceAnswer.Cancel) { cancelled = true; break; }

            if (answer == ReplaceAnswer.All)
            {
                replaced += _commands.ReplaceAll(query, with, match.Start);
                break;
            }

            if (answer == ReplaceAnswer.Yes)
            {
                _commands.ReplaceMatch(match, with);
                replaced++;
            }
            else
            {
                // Skipped. The caret already sits past the match — FindAgain left it
                // on the far end — so dropping the highlight is all that is needed for
                // the next search to resume beyond it rather than offering it again.
                _commands.ClearSelection();
            }
        }

        if (replaced == 0)
        {
            _commands.Move(() => _commands.Cursor.MoveTo(homeRow, homeCol), extend: false);
            _view.SetMessage(cancelled ? "Cancelled" : $"Not found: {query.Text}",
                             cancelled ? MessageKind.Info : MessageKind.Error);
        }
        else
            _view.Message = $"Replaced {replaced} occurrence{(replaced == 1 ? "" : "s")}"
                          + (cancelled ? " (cancelled)" : "");
    }

    // The shared front half of ^F and ^R: a prompt carrying the case and whole-word
    // toggles in its own label. Null on Esc or an empty term.
    private SearchQuery? PromptForQuery(string verb)
    {
        SearchState state = _commands.Search;
        string Label() => $"{verb}{Badges(state)}: ";

        string? entered = RunPrompt(Label(), state.Text, (ev, prompt) =>
        {
            if (!ev.Alt) return false;
            switch (ev.Key)
            {
                case ConsoleKey.C: state.MatchCase = !state.MatchCase; break;
                case ConsoleKey.W: state.WholeWord = !state.WholeWord; break;
                default: return false;
            }
            prompt.Label = Label(); // the badges are the only place this state shows
            return true;
        });

        if (string.IsNullOrEmpty(entered)) return null;
        return new SearchQuery(entered, state.MatchCase, state.WholeWord);
    }

    private static string Badges(SearchState state)
        => (state.MatchCase ? " [Aa]" : "") + (state.WholeWord ? " [W]" : "");

    // A found match needs no message: the highlight is the answer. The other three
    // outcomes do, and "not found" says so without the caret having moved.
    private void Report(SearchOutcome outcome, string term)
    {
        switch (outcome)
        {
            case SearchOutcome.FoundWrapped:
                _view.Message = "Search wrapped";
                break;
            case SearchOutcome.NotFound:
                _view.SetMessage($"Not found: {term}", MessageKind.Error);
                break;
            case SearchOutcome.NoQuery:
                _view.SetMessage("Nothing to search for — ^F first", MessageKind.Error);
                break;
        }
    }

    // ^W. Reports the selection when there is one and the whole buffer when there is
    // not, which is the question you are actually asking in each case — nobody
    // highlights a paragraph and then wants the file's total.
    //
    // A one-shot message rather than a permanent corner of the title row: the count
    // is O(buffer) and the title row is redrawn every frame, so a persistent version
    // would want a cache invalidated off LinesChanged to earn its place. This answers
    // the same question for none of that.
    private void ShowStats()
    {
        bool selected = _commands.HasSelection;
        TextStats stats = selected ? _commands.CountSelection() : TextStats.Of(_buffer);

        _view.Message = (selected ? "Selected: " : "")
            + $"{stats.Lines:N0} line{Plural(stats.Lines)}"
            + $"   {stats.Words:N0} word{Plural(stats.Words)}"
            + $"   {stats.Chars:N0} char{Plural(stats.Chars)}";
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    // nano-style line count: the trailing empty, unterminated line (from a file that
    // ended with a newline) is not a line the user thinks of as written. Shared with
    // ^W so the two can never report different totals for the same buffer.
    private int LinesWritten() => TextStats.LineCount(_buffer);

    // Ctrl+X. Clean buffer quits immediately; a modified one asks first.
    private bool TryQuit()
    {
        if (!_buffer.IsModified) return true;

        char? answer = Confirm("Save modified buffer?   Y: yes   N: no   Esc: cancel");
        return answer switch
        {
            'y' => DoSave(),   // only quit if the save succeeded
            'n' => true,       // discard changes
            _ => false,        // Esc / cancel
        };
    }

    // A modal line editor on the message row. Returns the entered text, or null on Esc.
    //
    // `onChord` gets first refusal on Ctrl/Alt combinations and returns whether it
    // consumed one. Without it the prompt swallows every chord (see the filter near
    // the bottom), which is right for Save-as and go-to-line and wrong for search,
    // where Alt+C and Alt+W have to toggle case and whole word mid-term.
    private string? RunPrompt(string label, string initial = "",
                              Func<InputEvent, Prompt, bool>? onChord = null)
    {
        var prompt = new Prompt(label, initial);
        _view.ActivePrompt = prompt;
        try
        {
            while (true)
            {
                Draw();
                _modalBatch.Clear();
                if (_reader.ReadBatch(_modalBatch) == 0) continue;

                foreach (InputEvent ev in _modalBatch)
                {
                    if (ev.Kind == InputEventKind.Resize) { _screen.Resize(ev.Width, ev.Height); continue; }
                    if (ev.Kind != InputEventKind.Key) continue;

                    switch (ev.Key)
                    {
                        case ConsoleKey.Escape: return null;
                        case ConsoleKey.Enter: return prompt.Input;
                        case ConsoleKey.Backspace: prompt.Backspace(); continue;
                        case ConsoleKey.Delete: prompt.Delete(); continue;
                        case ConsoleKey.LeftArrow: prompt.Left(); continue;
                        case ConsoleKey.RightArrow: prompt.Right(); continue;
                        case ConsoleKey.Home: prompt.Home(); continue;
                        case ConsoleKey.End: prompt.End(); continue;
                    }

                    if (ev.Ctrl || ev.Alt)
                    {
                        onChord?.Invoke(ev, prompt);
                        continue;
                    }

                    if (ev.Text is { Length: > 0 }) prompt.InsertText(ev.Text);
                    else if (ev.Char >= ' ' && ev.Char != '\x7f') prompt.InsertText(ev.Char.ToString());
                }
            }
        }
        finally
        {
            _view.ActivePrompt = null;
        }
    }

    // A yes/no/cancel question on the message row. 'y'/'n' or null (Esc).
    private char? Confirm(string message)
    {
        _view.SetMessage(message, MessageKind.Prompt);
        try
        {
            while (true)
            {
                Draw();
                _modalBatch.Clear();
                if (_reader.ReadBatch(_modalBatch) == 0) continue;

                foreach (InputEvent ev in _modalBatch)
                {
                    if (ev.Kind == InputEventKind.Resize) { _screen.Resize(ev.Width, ev.Height); continue; }
                    if (ev.Kind != InputEventKind.Key) continue;

                    if (ev.Key == ConsoleKey.Escape) return null;
                    char c = char.ToLowerInvariant(ev.Char);
                    if (c == 'y') return 'y';
                    if (c == 'n') return 'n';
                }
            }
        }
        finally
        {
            _view.Message = "";
        }
    }

    private enum ReplaceAnswer { Yes, No, All, Cancel }

    // Confirm's four-answer sibling, for replace. Like the other modal loops it draws
    // every iteration, so the match the question is about is highlighted and scrolled
    // into view before the question is asked — a "replace this one?" you cannot see
    // is not a question.
    private ReplaceAnswer ConfirmReplace()
    {
        _view.SetMessage("Replace this one?   Y: yes   N: skip   A: all   Esc: stop", MessageKind.Prompt);
        try
        {
            while (true)
            {
                Draw();
                _modalBatch.Clear();
                if (_reader.ReadBatch(_modalBatch) == 0) continue;

                foreach (InputEvent ev in _modalBatch)
                {
                    if (ev.Kind == InputEventKind.Resize) { _screen.Resize(ev.Width, ev.Height); continue; }
                    if (ev.Kind != InputEventKind.Key) continue;

                    if (ev.Key == ConsoleKey.Escape) return ReplaceAnswer.Cancel;
                    switch (char.ToLowerInvariant(ev.Char))
                    {
                        case 'y': return ReplaceAnswer.Yes;
                        case 'n': return ReplaceAnswer.No;
                        case 'a': return ReplaceAnswer.All;
                    }
                }
            }
        }
        finally
        {
            _view.Message = "";
        }
    }

    // ^H. The full keymap over the buffer until any key dismisses it. Painted through
    // the same Screen diff as everything else, so leaving it costs one ordinary frame
    // and there is nothing to restore by hand.
    private void ShowHelp()
    {
        while (true)
        {
            _view.RenderOverlay(HelpScreen.Title, HelpScreen.Lines, HelpScreen.Footer);
            _screen.Render(_host.Out);
            _host.Out.MoveTo(_view.CursorY + 1, _view.CursorX + 1);
            _host.Out.Flush();

            _modalBatch.Clear();
            if (_reader.ReadBatch(_modalBatch) == 0) continue;

            foreach (InputEvent ev in _modalBatch)
            {
                if (ev.Kind == InputEventKind.Resize) { _screen.Resize(ev.Width, ev.Height); continue; }
                if (ev.Kind == InputEventKind.Key) return;
            }
        }
    }
}
