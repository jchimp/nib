using Microsoft.Win32.SafeHandles;

namespace Nib.Terminal;

/// <summary>
/// Owns the console's raw mode and the alternate screen buffer.
///
/// The single most important property of this class is that Restore() runs on
/// EVERY exit path — normal return, unhandled exception, Ctrl+Break, and the
/// window close button. If we die while in raw mode we leave the user's shell
/// with no echo, no line editing and a hidden cursor, which is a genuinely
/// hostile thing to do to someone.
/// </summary>
public sealed class ConsoleHost : IDisposable
{
    // Opening CONIN$/CONOUT$ rather than using GetStdHandle means we still work
    // when stdin or stdout has been redirected (`nib file | more`, launched from
    // a wrapper script, etc). This is what nano does.
    private const string ConIn = "CONIN$";
    private const string ConOut = "CONOUT$";

    private const string AltScreenOn = "\x1b[?1049h";
    private const string AltScreenOff = "\x1b[?1049l";
    private const string CursorShow = "\x1b[?25h";
    private const string CursorHide = "\x1b[?25l";
    private const string SgrReset = "\x1b[0m";
    private const string ClearScreen = "\x1b[2J\x1b[H";

    // Must be a static field. If this delegate is only referenced from a local
    // or an instance that gets collected, the GC will reclaim it and Windows
    // will call into freed memory when the user closes the window.
    private static NativeMethods.ConsoleCtrlDelegate? s_ctrlHandler;
    private static ConsoleHost? s_current;

    private readonly uint _originalInputMode;
    private readonly uint _originalOutputMode;
    private int _restored;

    public SafeFileHandle InputHandle { get; }
    public SafeFileHandle OutputHandle { get; }
    public TerminalWriter Out { get; }

    /// <summary>True if raw mode was applied and Ctrl+C should now reach us as a keystroke.</summary>
    public bool CtrlCIntercepted { get; }

    public uint OriginalInputMode => _originalInputMode;
    public uint OriginalOutputMode => _originalOutputMode;
    public uint CurrentInputMode { get; }
    public uint CurrentOutputMode { get; }

    private ConsoleHost(
        SafeFileHandle input, SafeFileHandle output,
        uint origIn, uint origOut, uint newIn, uint newOut)
    {
        InputHandle = input;
        OutputHandle = output;
        _originalInputMode = origIn;
        _originalOutputMode = origOut;
        CurrentInputMode = newIn;
        CurrentOutputMode = newOut;
        CtrlCIntercepted = (newIn & NativeMethods.ENABLE_PROCESSED_INPUT) == 0;
        Out = new TerminalWriter(output);
    }

    public static ConsoleHost Acquire(bool enableMouse = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Nib targets the Windows console host.");

        SafeFileHandle input = NativeMethods.CreateFileW(
            ConIn,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            0, NativeMethods.OPEN_EXISTING, 0, 0);
        if (input.IsInvalid)
            throw new IOException("Could not open CONIN$. Nib must run attached to a console.");

        SafeFileHandle output = NativeMethods.CreateFileW(
            ConOut,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            0, NativeMethods.OPEN_EXISTING, 0, 0);
        if (output.IsInvalid)
        {
            input.Dispose();
            throw new IOException("Could not open CONOUT$. Nib must run attached to a console.");
        }

        if (!NativeMethods.GetConsoleMode(input, out uint origIn))
            throw new IOException("GetConsoleMode(CONIN$) failed.");
        if (!NativeMethods.GetConsoleMode(output, out uint origOut))
            throw new IOException("GetConsoleMode(CONOUT$) failed.");

        // --- input: raw ------------------------------------------------------
        // Clearing ENABLE_PROCESSED_INPUT is what stops the console from eating
        // Ctrl+C as a break signal and lets it arrive as an ordinary key event.
        //
        // ENABLE_VIRTUAL_TERMINAL_INPUT is deliberately CLEARED. It converts
        // keystrokes into VT escape sequences delivered through ReadFile. We use
        // ReadConsoleInputW instead, which gives structured records with virtual
        // key codes, modifier state, resize events and mouse events — strictly
        // more information than the escape-sequence stream, and no parser needed.
        //
        // ENABLE_QUICK_EDIT_MODE must be cleared to receive mouse input, and it
        // can only be cleared if ENABLE_EXTENDED_FLAGS is set in the same call.
        uint newIn = origIn;
        newIn &= ~(NativeMethods.ENABLE_PROCESSED_INPUT
                 | NativeMethods.ENABLE_LINE_INPUT
                 | NativeMethods.ENABLE_ECHO_INPUT
                 | NativeMethods.ENABLE_QUICK_EDIT_MODE
                 | NativeMethods.ENABLE_VIRTUAL_TERMINAL_INPUT);
        newIn |= NativeMethods.ENABLE_EXTENDED_FLAGS | NativeMethods.ENABLE_WINDOW_INPUT;
        if (enableMouse) newIn |= NativeMethods.ENABLE_MOUSE_INPUT;

        // --- output: VT ------------------------------------------------------
        // DISABLE_NEWLINE_AUTO_RETURN stops the console scrolling the whole
        // buffer when we write into the bottom-right cell, which a full-screen
        // renderer does on every single frame.
        uint newOut = origOut
                    | NativeMethods.ENABLE_PROCESSED_OUTPUT
                    | NativeMethods.ENABLE_VIRTUAL_TERMINAL_PROCESSING
                    | NativeMethods.DISABLE_NEWLINE_AUTO_RETURN;

        // Apply output first: if input mode then fails, roll output back so we
        // never leave the console half-configured on the way out.
        if (!NativeMethods.SetConsoleMode(output, newOut))
            throw new IOException(
                "Could not enable virtual terminal processing. Nib needs Windows 10 1511 or newer.");

        if (!NativeMethods.SetConsoleMode(input, newIn))
        {
            NativeMethods.SetConsoleMode(output, origOut);
            throw new IOException("Could not put the console into raw input mode.");
        }

        var host = new ConsoleHost(input, output, origIn, origOut, newIn, newOut);
        s_current = host;

        host.Out.Write(AltScreenOn);
        host.Out.Write(ClearScreen);
        host.Out.Flush();

        // Belt and braces: three independent paths back to Restore().
        s_ctrlHandler = OnConsoleCtrl;
        NativeMethods.SetConsoleCtrlHandler(s_ctrlHandler, true);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => s_current?.Restore();
        AppDomain.CurrentDomain.UnhandledException += static (_, _) => s_current?.Restore();

        return host;
    }

    // internal rather than private so the dispatch table can be asserted without a
    // console. Safe to call with no host acquired: Restore is null-guarded.
    internal static bool OnConsoleCtrl(uint ctrlType)
    {
        switch (ctrlType)
        {
            // Ctrl+C is a copy key here, and swallowing the event says so.
            //
            // Clearing ENABLE_PROCESSED_INPUT means this should never arrive at all:
            // Ctrl+C comes through ReadConsoleInputW as an ordinary key and no
            // CTRL_C_EVENT is generated. That made the input mode a single point of
            // failure with nothing behind it — anything that restored the flag for
            // even a moment would have Windows terminate us mid-edit, with the
            // buffer unsaved and no prompt. Returning true costs nothing when the
            // event never fires and saves the user's work when it does.
            //
            // Ctrl+Break deliberately still kills, so there is always a way out.
            case NativeMethods.CTRL_C_EVENT:
                return true;

            case NativeMethods.CTRL_CLOSE_EVENT:
            case NativeMethods.CTRL_LOGOFF_EVENT:
            case NativeMethods.CTRL_SHUTDOWN_EVENT:
            case NativeMethods.CTRL_BREAK_EVENT:
                s_current?.Restore();
                return false; // let the default handler continue tearing us down
            default:
                return false;
        }
    }

    public (int Width, int Height) GetWindowSize()
    {
        if (!NativeMethods.GetConsoleScreenBufferInfo(OutputHandle, out var info))
            return (80, 25);
        int w = info.srWindow.Right - info.srWindow.Left + 1;
        int h = info.srWindow.Bottom - info.srWindow.Top + 1;
        return (Math.Max(w, 1), Math.Max(h, 1));
    }

    public void HideCursor() => Out.Write(CursorHide);

    public void ShowCursor() => Out.Write(CursorShow);

    /// <summary>Idempotent. Safe to call from any thread and from any number of exit paths.</summary>
    public void Restore()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0) return;

        try
        {
            Out.Write(SgrReset);
            Out.Write(CursorShow);
            Out.Write(AltScreenOff);
            Out.Flush();
        }
        catch
        {
            // Nothing useful to do — we are already on the way out.
        }

        NativeMethods.SetConsoleMode(InputHandle, _originalInputMode);
        NativeMethods.SetConsoleMode(OutputHandle, _originalOutputMode);
        NativeMethods.SetConsoleCtrlHandler(s_ctrlHandler, false);
        s_ctrlHandler = null;
        if (ReferenceEquals(s_current, this)) s_current = null;
    }

    public void Dispose()
    {
        Restore();
        InputHandle.Dispose();
        OutputHandle.Dispose();
    }
}
