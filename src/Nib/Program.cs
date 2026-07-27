using Nib.Commands;
using Nib.Model;
using Nib.Terminal;
using Nib.Ui;

namespace Nib;

/// <summary>
/// Entry point and the phase-3 editor loop. <c>nib &lt;file&gt;</c> opens a file for
/// editing (or an empty buffer with no argument); <c>nib --probe</c> runs the
/// phase-1 terminal probe. Loading and saving go through <see cref="FileIo"/>, so
/// bytes, encoding, and line endings are preserved.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        bool probe = false;
        string? file = null;

        foreach (string a in args)
        {
            switch (a)
            {
                case "--probe": probe = true; break;
                case "--help" or "-h": PrintHelp(); return 0;
                default:
                    if (!a.StartsWith('-')) file ??= a;
                    break;
            }
        }

        // Load before touching the console: a bad path should print to a normal
        // shell, not from inside the alternate screen.
        TextBuffer? buffer = null;
        if (!probe)
        {
            try
            {
                buffer = file is null ? TextBuffer.Empty() : FileIo.Load(file);
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
            else new Editor(host, buffer!).Run();
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
        Console.WriteLine("nib — a console text editor (phase 3: editing core)");
        Console.WriteLine();
        Console.WriteLine("usage: nib [options] [file]");
        Console.WriteLine();
        Console.WriteLine("  --probe   run the phase-1 terminal probe");
        Console.WriteLine("  -h, --help");
        Console.WriteLine();
        Console.WriteLine("keys: arrows / Home / End / PgUp / PgDn move; Ctrl+arrows by word;");
        Console.WriteLine("      Ctrl+S or Ctrl+O save; Ctrl+X quit; Ctrl+G help.");
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
    private readonly List<InputEvent> _batch = new(256);

    public Editor(ConsoleHost host, TextBuffer buffer)
    {
        _host = host;
        _buffer = buffer;
        (int w, int h) = host.GetWindowSize();
        _screen = new Screen(w, h);
        _viewport = new Viewport();
        _cursor = new Cursor(buffer, _viewport.TabWidth);
        _view = new EditorView(_screen, _viewport, buffer, _cursor);
        _commands = new EditorCommands(buffer, _cursor);
        _reader = new InputReader(host);
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

                switch (Keymap.Handle(ev, _commands, pageRows))
                {
                    case EditorAction.Save: DoSave(); break;
                    case EditorAction.Quit: if (TryQuit()) return; break;
                    case EditorAction.Help: _view.Message = "^S/^O Save   ^X Exit   arrows move   Ctrl+arrows word   (full help: later phase)"; break;
                }
            }

            Draw();
        }
    }

    // One frame: clamp/scroll to the caret, paint, then place the hardware cursor
    // where the view computed it, and flush once.
    private void Draw()
    {
        _viewport.ClampVertical(_buffer.LineCount);
        _viewport.EnsureVisible(_cursor.Row, _cursor.DisplayColumn, _view.TextRows, _screen.Width);
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
                _batch.Clear();
                if (_reader.ReadBatch(_batch) == 0) continue;

                foreach (InputEvent ev in _batch)
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
                _batch.Clear();
                if (_reader.ReadBatch(_batch) == 0) continue;

                foreach (InputEvent ev in _batch)
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
