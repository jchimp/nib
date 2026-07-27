using Nib.Terminal;

namespace Nib;

/// <summary>
/// The phase-1 acceptance probe, reachable as <c>nib --probe</c>. Not an editor:
/// it proves every piece of Win32 that can ruin the project, run interactively so
/// you can watch it work:
///
///   1. Raw mode goes on and comes back off cleanly.
///   2. Ctrl+C arrives as a keystroke instead of killing the process.
///   3. Window resize arrives as an event.
///   4. The clipboard round-trips text.
///   5. Quitting leaves the shell exactly as it was found.
///
/// Kept around past phase 1 as a hardware regression check for the terminal
/// foundation the renderer sits on.
/// </summary>
internal static class Probe
{
    private const int MaxLogLines = 14;

    public static void Run(ConsoleHost host)
    {
        var reader = new InputReader(host);
        var batch = new List<InputEvent>(256);
        var log = new List<string>(MaxLogLines);

        // Everything below is probe state: what we've observed so far. The Draw
        // local function renders it, and the loop mutates it as events arrive.
        (int width, int height) = host.GetWindowSize();
        int keyCount = 0;
        string clipboardStatus = "not tested";
        string lastPaste = "";
        bool sawResize = false;
        bool sawCtrlC = false;

        host.HideCursor();
        Draw();

        // Event-driven redraw: block for input, apply the batch, repaint once.
        // There is no animation or timer here, so we only ever draw in response
        // to something the user did.
        while (true)
        {
            batch.Clear();
            // A batch can decode to zero events (all modifier or key-up records);
            // nothing to show, so go straight back to blocking.
            if (reader.ReadBatch(batch) == 0) continue;

            foreach (InputEvent ev in batch)
            {
                switch (ev.Kind)
                {
                    case InputEventKind.Resize:
                        width = ev.Width;
                        height = ev.Height;
                        sawResize = true;
                        Append($"RESIZE  {width}x{height}");
                        break;

                    case InputEventKind.Mouse:
                        // Logged but not otherwise used until phase 6.
                        if (ev.MouseAction != MouseAction.Move) Append("MOUSE   " + ev.Describe());
                        break;

                    case InputEventKind.Key:
                        keyCount++;
                        Append($"KEY     {ev.Describe(),-22} vk=0x{(int)ev.Key:X2} " +
                               $"ch={Printable(ev.Char)}");

                        // Ctrl+X is the only exit. Returning here unwinds to the
                        // caller, which restores the console.
                        if (ev.Ctrl && ev.Key == ConsoleKey.X) { Draw(); return; }

                        // Ctrl+C is the point of the whole probe: proving it lands
                        // here as a keystroke instead of terminating the process.
                        // We piggyback the clipboard copy test on it.
                        if (ev.Ctrl && ev.Key == ConsoleKey.C)
                        {
                            sawCtrlC = true;
                            string probe = $"nib clipboard probe #{keyCount}";
                            bool ok = Clipboard.SetText(probe);
                            clipboardStatus = ok
                                ? $"copied \"{probe}\""
                                : "copy FAILED (using fallback buffer)";
                        }

                        if (ev.Ctrl && ev.Key == ConsoleKey.V)
                        {
                            string text = Clipboard.GetText();
                            lastPaste = Summarize(text);
                            clipboardStatus = Clipboard.LastOperationUsedSystemClipboard
                                ? "pasted from system clipboard"
                                : "pasted from fallback buffer";
                        }
                        break;
                }
            }

            Draw();
        }

        // Fixed-height scrolling log: drop the oldest line once we're full.
        void Append(string line)
        {
            log.Add(line);
            if (log.Count > MaxLogLines) log.RemoveAt(0);
        }

        // Full repaint. The probe has no diffing (that's the renderer) — we clear
        // and redraw the whole screen each time, which is fine at this event rate.
        void Draw()
        {
            TerminalWriter o = host.Out;
            o.ClearScreen();

            int row = 1;
            Heading(o, ref row, $"Nib — phase 1 terminal probe   ({width}x{height})");

            Line(o, ref row, $"stdin  mode  0x{host.OriginalInputMode:X8} -> 0x{host.CurrentInputMode:X8}");
            Line(o, ref row, $"stdout mode  0x{host.OriginalOutputMode:X8} -> 0x{host.CurrentOutputMode:X8}");
            row++;

            Check(o, ref row, "Ctrl+C reaches the app (not SIGINT)", host.CtrlCIntercepted && sawCtrlC,
                  host.CtrlCIntercepted ? "mode set — press Ctrl+C to confirm" : "PROCESSED_INPUT still set");
            Check(o, ref row, "Resize events delivered", sawResize, "drag the window edge");
            Line(o, ref row, $"  clipboard  {clipboardStatus}");
            if (lastPaste.Length > 0) Line(o, ref row, $"  last paste \"{lastPaste}\"");
            row++;

            Heading(o, ref row, "event log");
            foreach (string line in log) Line(o, ref row, "  " + line);

            o.MoveTo(Math.Max(height, 2), 1);
            o.Foreground(0x80, 0x80, 0x80);
            o.Write("Ctrl+C copy · Ctrl+V paste · resize the window · Ctrl+X to quit");
            o.ResetStyle();
            o.Flush();
        }
    }

    private static void Heading(TerminalWriter o, ref int row, string text)
    {
        o.MoveTo(row++, 1);
        o.Foreground(0x4E, 0xC9, 0xB0);
        o.Write(text);
        o.ResetStyle();
        o.MoveTo(row++, 1);
        o.Foreground(0x50, 0x50, 0x50);
        o.Write(new string('-', Math.Min(text.Length + 8, 72)));
        o.ResetStyle();
    }

    private static void Line(TerminalWriter o, ref int row, string text)
    {
        o.MoveTo(row++, 1);
        o.Write(text);
    }

    private static void Check(TerminalWriter o, ref int row, string label, bool ok, string hint)
    {
        o.MoveTo(row++, 1);
        if (ok) { o.Foreground(0x4E, 0xC9, 0x50); o.Write("  [ok]   "); }
        else { o.Foreground(0xC0, 0xA0, 0x40); o.Write("  [....] "); }
        o.ResetStyle();
        o.Write(label);
        if (!ok) { o.Write("  — "); o.Write(hint); }
    }

    private static string Printable(char c) =>
        c == '\0' ? "--" : c < ' ' ? $"0x{(int)c:X2}" : $"'{c}'";

    private static string Summarize(string text)
    {
        string flat = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return flat.Length <= 48 ? flat : flat[..48] + "…";
    }
}
