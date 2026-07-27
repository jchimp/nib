using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nib.Terminal;

/// <summary>
/// Raw Win32 surface. Nothing in here makes policy decisions — it is a literal
/// transcription of the console and clipboard APIs. Policy lives in ConsoleHost,
/// InputReader and Clipboard.
/// </summary>
internal static class NativeMethods
{
    // ---- Console input modes -------------------------------------------------
    internal const uint ENABLE_PROCESSED_INPUT = 0x0001;
    internal const uint ENABLE_LINE_INPUT = 0x0002;
    internal const uint ENABLE_ECHO_INPUT = 0x0004;
    internal const uint ENABLE_WINDOW_INPUT = 0x0008;
    internal const uint ENABLE_MOUSE_INPUT = 0x0010;
    internal const uint ENABLE_INSERT_MODE = 0x0020;
    internal const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    internal const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    internal const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    // ---- Console output modes ------------------------------------------------
    internal const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    internal const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;
    internal const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    internal const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    // ---- CreateFile ----------------------------------------------------------
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    // ---- INPUT_RECORD event types -------------------------------------------
    internal const ushort KEY_EVENT = 0x0001;
    internal const ushort MOUSE_EVENT = 0x0002;
    internal const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;
    internal const ushort MENU_EVENT = 0x0008;
    internal const ushort FOCUS_EVENT = 0x0010;

    // ---- dwControlKeyState ---------------------------------------------------
    internal const uint RIGHT_ALT_PRESSED = 0x0001;
    internal const uint LEFT_ALT_PRESSED = 0x0002;
    internal const uint RIGHT_CTRL_PRESSED = 0x0004;
    internal const uint LEFT_CTRL_PRESSED = 0x0008;
    internal const uint SHIFT_PRESSED = 0x0010;
    internal const uint NUMLOCK_ON = 0x0020;
    internal const uint SCROLLLOCK_ON = 0x0040;
    internal const uint CAPSLOCK_ON = 0x0080;
    internal const uint ENHANCED_KEY = 0x0100;

    // ---- Mouse ---------------------------------------------------------------
    internal const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;
    internal const uint RIGHTMOST_BUTTON_PRESSED = 0x0002;
    internal const uint MOUSE_MOVED = 0x0001;
    internal const uint DOUBLE_CLICK = 0x0002;
    internal const uint MOUSE_WHEELED = 0x0004;
    internal const uint MOUSE_HWHEELED = 0x0008;

    // ---- Console control events ---------------------------------------------
    internal const uint CTRL_C_EVENT = 0;
    internal const uint CTRL_BREAK_EVENT = 1;
    internal const uint CTRL_CLOSE_EVENT = 2;
    internal const uint CTRL_LOGOFF_EVENT = 5;
    internal const uint CTRL_SHUTDOWN_EVENT = 6;

    // ---- Clipboard -----------------------------------------------------------
    internal const uint CF_UNICODETEXT = 13;
    internal const uint GMEM_MOVEABLE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    // BOOL is marshalled as int deliberately: a 4-byte field with an explicit
    // layout is safer here than letting the marshaller pick a size for bool.
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOW_BUFFER_SIZE_RECORD
    {
        public COORD dwSize;
    }

    // The union begins at offset 4: EventType is a WORD, but KEY_EVENT_RECORD
    // leads with a DWORD-aligned BOOL, so the compiler pads to 4.
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
        [FieldOffset(4)] public WINDOW_BUFFER_SIZE_RECORD WindowBufferSizeEvent;
    }

    internal delegate bool ConsoleCtrlDelegate(uint ctrlType);

    // ---- kernel32 ------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleMode(SafeFileHandle hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleMode(SafeFileHandle hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadConsoleInputW(
        SafeFileHandle hConsoleInput,
        [Out] INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfConsoleInputEvents(
        SafeFileHandle hConsoleInput, out uint lpcNumberOfEvents);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern unsafe bool WriteConsoleW(
        SafeFileHandle hConsoleOutput,
        char* lpBuffer,
        uint nNumberOfCharsToWrite,
        out uint lpNumberOfCharsWritten,
        nint lpReserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleScreenBufferInfo(
        SafeFileHandle hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(
        ConsoleCtrlDelegate? handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalFree(nint hMem);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(nint hMem);

    // ---- user32 (clipboard) --------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);
}
