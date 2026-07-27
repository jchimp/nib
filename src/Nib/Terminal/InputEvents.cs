using System.Text;

namespace Nib.Terminal;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

public enum InputEventKind
{
    Key,
    Resize,
    Mouse,
}

public enum MouseAction
{
    Move,
    ButtonDown,
    ButtonUp,
    Wheel,
    DoubleClick,
}

/// <summary>
/// One decoded input event. A struct so the read loop can drain a batch into a
/// pooled list without allocating per keystroke.
/// </summary>
public readonly struct InputEvent
{
    public InputEventKind Kind { get; init; }

    // Key
    public ConsoleKey Key { get; init; }
    public char Char { get; init; }
    public KeyModifiers Modifiers { get; init; }

    /// <summary>
    /// Set only when the keystroke produced a character outside the BMP, which
    /// arrives as two consecutive records carrying a surrogate pair. Char is
    /// '\0' in that case.
    /// </summary>
    public string? Text { get; init; }

    // Resize
    public int Width { get; init; }
    public int Height { get; init; }

    // Mouse
    public MouseAction MouseAction { get; init; }
    public int MouseX { get; init; }
    public int MouseY { get; init; }
    public int WheelDelta { get; init; }

    public bool Ctrl => (Modifiers & KeyModifiers.Control) != 0;
    public bool Shift => (Modifiers & KeyModifiers.Shift) != 0;
    public bool Alt => (Modifiers & KeyModifiers.Alt) != 0;

    /// <summary>Human-readable chord, e.g. "Ctrl+Shift+K". For the keymap table and the phase-1 probe.</summary>
    public string Describe()
    {
        if (Kind == InputEventKind.Resize) return $"Resize {Width}x{Height}";
        if (Kind == InputEventKind.Mouse) return $"Mouse {MouseAction} ({MouseX},{MouseY}) wheel={WheelDelta}";

        var sb = new StringBuilder(24);
        if (Ctrl) sb.Append("Ctrl+");
        if (Alt) sb.Append("Alt+");
        if (Shift) sb.Append("Shift+");

        if (Text is not null) sb.Append(Text);
        else if (Key != 0) sb.Append(Key.ToString());
        else if (Char >= ' ') sb.Append(Char);
        else sb.Append("(none)");

        return sb.ToString();
    }
}
