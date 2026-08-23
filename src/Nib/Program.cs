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
        bool probe = false;
        bool soak = false;
        string? file = null;
        string? theme = null;
        int startLine = 0; // 0 = unset; nano's +LINE
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--probe": probe = true; break;
                case "--soak": soak = true; break;
                case "--theme": theme = i + 1 < args.Length ? args[++i] : null; break;
                case "--help" or "-h": PrintHelp(); return 0;
                case "--version" or "-v": PrintVersion(); return 0;
                default:
                    // `+123` opens at a line, as nano does. It does not start with '-',
                    // so without this it would be taken for the filename.
                    if (a.Length > 1 && a[0] == '+' && int.TryParse(a[1..], out int n) && n > 0)
                        startLine = n;
                    else if (!a.StartsWith('-')) { positional.Add(a); file ??= a; }
                    break;
            }
        }

        // Diagnostic path: no console acquisition, ordinary stdout. See Soak.
        if (soak)
        {
            string? dir = positional.Count > 0 ? positional[0] : null;
            int passes = positional.Count > 1 && int.TryParse(positional[1], out int p) ? p : 3;
            return Highlight.Soak.Run(dir, passes);
        }

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

        ConsoleHost host;
        try
        {
            host = ConsoleHost.Acquire();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nib: {ex.Message}");
            return 1;
        }

        try
        {
            if (probe) Probe.Run(host);
            else new Editor(host, buffer!, theme, openingMessage, startLine).Run();
            return 0;
        }
        finally
        {
            // Primary restore path; ConsoleHost also hooks ProcessExit,
            // UnhandledException and the console control handler.
            host.Dispose();
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("nib — a console text editor");
        Console.WriteLine();
        Console.WriteLine("usage: nib [options] [+LINE] [file]");
        Console.WriteLine();
        Console.WriteLine("  --theme <id>   dark-plus | light-plus | monokai | solarized-dark | high-contrast");
        Console.WriteLine("  --probe        run the phase-1 terminal probe");
        Console.WriteLine("  --soak [dir] [passes]");
        Console.WriteLine("                 tokenizer soak test; not part of the editor");
        Console.WriteLine("  -h, --help");
        Console.WriteLine("  -v, --version");
        Console.WriteLine();
        Console.WriteLine("A file that does not exist opens as a new, empty buffer under that name.");
        Console.WriteLine();
        Console.WriteLine("keys: arrows / Home / End / PgUp / PgDn move; Ctrl+arrows by word;");
        Console.WriteLine("      Shift+move selects; Esc clears the selection; Ctrl+A select all;");
        Console.WriteLine("      Ctrl+C/X/V copy/cut/paste; Ctrl+K/U cut/paste line; Ctrl+Z/Y undo/redo;");
        Console.WriteLine("      Ctrl+S or Ctrl+O save; Ctrl+Q quit; Ctrl+X cut, or quit with no selection;");
        Console.WriteLine("      Ctrl+G go to line; Ctrl+H help; Alt+T cycle theme.");
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
        int startLine = 0)
    {
        _host = host;
        _buffer = buffer;
        (int w, int h) = host.GetWindowSize();
        _screen = new Screen(w, h);
        _viewport = new Viewport();
        _cursor = new Cursor(buffer, _viewport.TabWidth);
        _view = new EditorView(_screen, _viewport, buffer, _cursor);
        _commands = new EditorCommands(buffer, _cursor, new SystemClipboard());
        _view.Selection = _commands.Selection; // the view paints the live selection
        _reader = new InputReader(host);

        // Falls back to NullHighlighter for an unknown file type or a bad resource:
        // no colour, no error, no reason for the user to care.
        _highlighter = TextMateHighlighter.Create(buffer, themeId);
        _view.Highlighter = _highlighter;

        // `+LINE` past the end of the file lands on the last line rather than
        // refusing — the prompt reports out-of-range, but a command line is not a
        // conversation and silently doing the nearest sensible thing is nano's habit.
        if (startLine > 0) _commands.TryGoToLine(Math.Min(startLine, buffer.LineCount));

        // Cleared by the next keystroke, which is the right lifetime for "New File".
        _view.Message = openingMessage ?? "";
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
                        case EditorAction.Help: _view.Message = "Select: Shift+arrows  Cut/Copy/Paste: ^X/^C/^V  Undo/Redo: ^Z/^Y  Line: ^K/^U  All: ^A  Go to: ^G  Exit: ^Q  Theme: Alt+T"; break;
                        case EditorAction.GoToLine: DoGoToLine(); break;
                        case EditorAction.CycleTheme: CycleTheme(); break;
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
        _view.Message = $"Internal error ({ex.GetType().Name}: {ex.Message}) — your text is intact, ^S to save";
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
        _viewport.EnsureVisible(_cursor.Row, _cursor.DisplayColumn, _view.TextRows, _screen.Width);
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
            _view.Message = $"Error: {ex.Message}";
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
            _view.Message = $"Not a line number: {entered}";
            return;
        }

        if (!_commands.TryGoToLine(line))
            _view.Message = $"No line {line} (buffer has {_buffer.LineCount})";
    }

    // nano-style line count: the trailing empty, unterminated line (from a file
    // that ended with a newline) is not a line the user thinks of as written.
    private int LinesWritten()
    {
        int count = _buffer.LineCount;
        if (count > 1)
        {
            Line last = _buffer.LineAt(count - 1);
            if (last.Text.Length == 0 && last.Ending == LineEnding.None) count--;
        }
        return count;
    }

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
    private string? RunPrompt(string label, string initial = "")
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

                    if (ev.Ctrl || ev.Alt) continue;
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
        _view.Message = message;
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
}
