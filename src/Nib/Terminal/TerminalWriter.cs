using Microsoft.Win32.SafeHandles;

namespace Nib.Terminal;

/// <summary>
/// A single-buffer console writer. Everything for one frame is appended here and
/// flushed with one WriteConsoleW call.
///
/// WriteConsoleW takes UTF-16 directly, so there is no console code page to set
/// and no encoding to get wrong — a real advantage over writing UTF-8 bytes
/// through a FileStream, which only works if the output code page happens to be
/// 65001.
///
/// Per-character Console.Write is the classic way to make a Windows TUI feel
/// sluggish. Do not add a convenience method that flushes.
/// </summary>
public sealed class TerminalWriter
{
    private readonly SafeFileHandle _handle;
    private char[] _buffer;
    private int _length;

    public TerminalWriter(SafeFileHandle handle, int initialCapacity = 32 * 1024)
    {
        _handle = handle;
        _buffer = new char[initialCapacity];
    }

    /// <summary>
    /// Test-only writer with no console behind it. The diff in <see cref="Nib.Terminal.Screen"/>
    /// emits VT into this buffer, and a test reads it back with <see cref="DebugSnapshot"/>;
    /// Flush is never called, so the invalid handle is never touched.
    /// </summary>
    internal TerminalWriter() : this(new SafeFileHandle(nint.Zero, ownsHandle: false)) { }

    public int PendingChars => _length;

    /// <summary>Test-only: the pending frame as a string, without flushing it to a console.</summary>
    internal string DebugSnapshot() => new(_buffer, 0, _length);

    private void Ensure(int extra)
    {
        int needed = _length + extra;
        if (needed <= _buffer.Length) return;
        int size = _buffer.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buffer, size);
    }

    public void Write(ReadOnlySpan<char> text)
    {
        Ensure(text.Length);
        text.CopyTo(_buffer.AsSpan(_length));
        _length += text.Length;
    }

    public void Write(char c)
    {
        Ensure(1);
        _buffer[_length++] = c;
    }

    /// <summary>Non-negative integers only. Avoids the allocation of int.ToString().</summary>
    public void Write(int value)
    {
        if (value < 0) { Write('-'); value = -value; }
        // Digits come out least-significant first, so build into the back of a
        // stack buffer and write the filled tail. 11 chars fits any int32.
        Span<char> tmp = stackalloc char[11];
        int i = tmp.Length;
        do { tmp[--i] = (char)('0' + value % 10); value /= 10; } while (value > 0);
        Write(tmp[i..]);
    }

    /// <summary>Move the cursor. Row and column are 1-based, matching the VT convention.</summary>
    public void MoveTo(int row, int column)
    {
        Write("\x1b[");
        Write(row);
        Write(';');
        Write(column);
        Write('H');
    }

    public void ClearLine() => Write("\x1b[2K");

    public void ClearScreen() => Write("\x1b[2J\x1b[H");

    public void ResetStyle() => Write("\x1b[0m");

    /// <summary>24-bit foreground. Windows Terminal renders this exactly; legacy conhost approximates.</summary>
    public void Foreground(byte r, byte g, byte b)
    {
        Write("\x1b[38;2;");
        Write(r); Write(';'); Write(g); Write(';'); Write(b); Write('m');
    }

    public void Background(byte r, byte g, byte b)
    {
        Write("\x1b[48;2;");
        Write(r); Write(';'); Write(g); Write(';'); Write(b); Write('m');
    }

    /// <summary>Reset foreground to the terminal's default. The renderer emits this for a default-color cell rather than guessing an RGB value.</summary>
    public void ForegroundDefault() => Write("\x1b[39m");

    public void BackgroundDefault() => Write("\x1b[49m");

    public unsafe void Flush()
    {
        if (_length == 0) return;

        fixed (char* p = _buffer)
        {
            // WriteConsoleW may write fewer chars than asked, so loop until the
            // whole buffer is out. `fixed` pins the array so the raw pointer
            // stays valid across the calls.
            int offset = 0;
            while (offset < _length)
            {
                if (!NativeMethods.WriteConsoleW(
                        _handle, p + offset, (uint)(_length - offset), out uint written, 0))
                {
                    // Console is gone (window closed mid-frame). Drop the frame
                    // rather than throwing from a teardown path.
                    break;
                }
                if (written == 0) break;
                offset += (int)written;
            }
        }

        _length = 0;
    }
}
