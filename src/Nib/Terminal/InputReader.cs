namespace Nib.Terminal;

/// <summary>
/// Turns raw INPUT_RECORDs into InputEvents.
///
/// Reads happen in batches. This matters more than it looks: pasting into a
/// console with Ctrl+Shift+V delivers the clipboard as a burst of individual
/// key events, and handling them one render frame at a time makes a paste of a
/// few hundred lines visibly crawl.
/// </summary>
public sealed class InputReader
{
    private readonly ConsoleHost _host;
    private readonly NativeMethods.INPUT_RECORD[] _records = new NativeMethods.INPUT_RECORD[256];
    private char _pendingHighSurrogate;

    public InputReader(ConsoleHost host) => _host = host;

    /// <summary>
    /// Blocks until at least one event is available, then drains everything
    /// currently queued into <paramref name="into"/>. Returns the number added,
    /// which can be zero if the batch was entirely modifier keys or key-up
    /// records.
    /// </summary>
    public int ReadBatch(List<InputEvent> into)
    {
        if (!NativeMethods.ReadConsoleInputW(
                _host.InputHandle, _records, (uint)_records.Length, out uint count))
        {
            return 0;
        }

        // One native read can return many records; translate each into at most
        // one InputEvent. `ref` avoids copying the ~20-byte union per iteration.
        int added = 0;
        for (int i = 0; i < count; i++)
        {
            ref NativeMethods.INPUT_RECORD rec = ref _records[i];
            switch (rec.EventType)
            {
                case NativeMethods.KEY_EVENT:
                    if (TryDecodeKey(in rec.KeyEvent, out InputEvent key))
                    {
                        into.Add(key);
                        added++;
                    }
                    break;

                case NativeMethods.WINDOW_BUFFER_SIZE_EVENT:
                {
                    // The record carries the BUFFER size. For a full-screen app on
                    // the alternate screen that usually equals the window, but
                    // re-querying is cheap and is the value we actually want.
                    (int w, int h) = _host.GetWindowSize();
                    into.Add(new InputEvent
                    {
                        Kind = InputEventKind.Resize,
                        Width = w,
                        Height = h,
                    });
                    added++;
                    break;
                }

                case NativeMethods.MOUSE_EVENT:
                    into.Add(DecodeMouse(in rec.MouseEvent));
                    added++;
                    break;

                // MENU_EVENT and FOCUS_EVENT are documented as "used internally,
                // should be ignored".
            }
        }

        return added;
    }

    private bool TryDecodeKey(in NativeMethods.KEY_EVENT_RECORD k, out InputEvent ev)
    {
        ev = default;

        // Only key-down interests an editor. Key-up records double the volume
        // and carry nothing we act on.
        if (k.bKeyDown == 0) return false;

        // Modifier keys on their own are not events an editor cares about.
        switch (k.wVirtualKeyCode)
        {
            case 0x10: // VK_SHIFT
            case 0x11: // VK_CONTROL
            case 0x12: // VK_MENU (Alt)
            case 0x14: // VK_CAPITAL
            case 0x5B: // VK_LWIN
            case 0x5C: // VK_RWIN
            case 0x90: // VK_NUMLOCK
            case 0x91: // VK_SCROLL
                return false;
        }

        uint state = k.dwControlKeyState;
        bool leftCtrl = (state & NativeMethods.LEFT_CTRL_PRESSED) != 0;
        bool rightCtrl = (state & NativeMethods.RIGHT_CTRL_PRESSED) != 0;
        bool leftAlt = (state & NativeMethods.LEFT_ALT_PRESSED) != 0;
        bool rightAlt = (state & NativeMethods.RIGHT_ALT_PRESSED) != 0;
        bool shift = (state & NativeMethods.SHIFT_PRESSED) != 0;

        bool ctrl = leftCtrl || rightCtrl;
        bool alt = leftAlt || rightAlt;

        // AltGr. On European layouts AltGr reports as RightAlt + LeftCtrl, so the
        // naive reading is that the user pressed Ctrl+Alt+something. If it also
        // produced a printable character, it is a character, not a chord.
        // Without this, a German user cannot type @ or \.
        if (rightAlt && leftCtrl && k.UnicodeChar != '\0')
        {
            ctrl = false;
            alt = false;
        }

        char ch = k.UnicodeChar;

        // Astral-plane characters arrive as two records. Hold the high half and
        // emit once the low half turns up.
        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            return false;
        }

        // Second half of a pair: recombine with the stored high half into one
        // string (Text), and null out Char since a lone surrogate isn't a usable
        // character. Any other low surrogate, or a stale pending half, is dropped.
        string? text = null;
        if (char.IsLowSurrogate(ch) && _pendingHighSurrogate != '\0')
        {
            text = new string(new[] { _pendingHighSurrogate, ch });
            _pendingHighSurrogate = '\0';
            ch = '\0';
        }
        else
        {
            _pendingHighSurrogate = '\0';
        }

        KeyModifiers mods = KeyModifiers.None;
        if (ctrl) mods |= KeyModifiers.Control;
        if (alt) mods |= KeyModifiers.Alt;
        if (shift) mods |= KeyModifiers.Shift;

        // When Ctrl is held the console reports the control code in UnicodeChar
        // (Ctrl+A is 0x01). The virtual key code is the useful identity; blank
        // the char so nothing downstream inserts a control character.
        if (ctrl && ch != '\0' && ch < ' ') ch = '\0';

        ev = new InputEvent
        {
            Kind = InputEventKind.Key,
            Key = (ConsoleKey)k.wVirtualKeyCode,
            Char = ch,
            Text = text,
            Modifiers = mods,
        };
        return true;
    }

    private static InputEvent DecodeMouse(in NativeMethods.MOUSE_EVENT_RECORD m)
    {
        MouseAction action;
        int wheel = 0;

        // dwEventFlags is a bitfield, but the actions are mutually exclusive in
        // practice, so first-match wins. Order matters only in that a plain
        // button press has no flag set at all, hence the final else.
        if ((m.dwEventFlags & NativeMethods.MOUSE_WHEELED) != 0)
        {
            action = MouseAction.Wheel;
            // High word of dwButtonState is a signed delta, 120 per detent.
            wheel = (short)((m.dwButtonState >> 16) & 0xFFFF) / 120;
        }
        else if ((m.dwEventFlags & NativeMethods.DOUBLE_CLICK) != 0)
        {
            action = MouseAction.DoubleClick;
        }
        else if ((m.dwEventFlags & NativeMethods.MOUSE_MOVED) != 0)
        {
            action = MouseAction.Move;
        }
        else
        {
            action = m.dwButtonState != 0 ? MouseAction.ButtonDown : MouseAction.ButtonUp;
        }

        KeyModifiers mods = KeyModifiers.None;
        if ((m.dwControlKeyState & NativeMethods.SHIFT_PRESSED) != 0) mods |= KeyModifiers.Shift;
        if ((m.dwControlKeyState & (NativeMethods.LEFT_CTRL_PRESSED | NativeMethods.RIGHT_CTRL_PRESSED)) != 0)
            mods |= KeyModifiers.Control;
        if ((m.dwControlKeyState & (NativeMethods.LEFT_ALT_PRESSED | NativeMethods.RIGHT_ALT_PRESSED)) != 0)
            mods |= KeyModifiers.Alt;

        return new InputEvent
        {
            Kind = InputEventKind.Mouse,
            MouseAction = action,
            MouseX = m.dwMousePosition.X,
            MouseY = m.dwMousePosition.Y,
            WheelDelta = wheel,
            Modifiers = mods,
        };
    }
}
