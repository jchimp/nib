using Nib.Terminal;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The raw-mode input flags. Everything else about console setup needs a live
/// console; this is pure arithmetic over the mode word, and it is where the two
/// documented landmines live — clearing ENABLE_PROCESSED_INPUT is the whole reason
/// Ctrl+C reaches us as a key, and ENABLE_QUICK_EDIT_MODE can only be cleared with
/// ENABLE_EXTENDED_FLAGS set in the same call.
/// </summary>
public class ConsoleInputModeTests
{
    // What a console typically hands us: cooked input with quick-edit on.
    private const uint Typical =
        NativeMethods.ENABLE_PROCESSED_INPUT
        | NativeMethods.ENABLE_LINE_INPUT
        | NativeMethods.ENABLE_ECHO_INPUT
        | NativeMethods.ENABLE_QUICK_EDIT_MODE;

    [Fact]
    public void Cooked_input_flags_are_always_cleared()
    {
        foreach (bool mouse in new[] { true, false })
        {
            uint mode = ConsoleHost.InputMode(Typical, mouse);
            Assert.Equal(0u, mode & NativeMethods.ENABLE_PROCESSED_INPUT);
            Assert.Equal(0u, mode & NativeMethods.ENABLE_LINE_INPUT);
            Assert.Equal(0u, mode & NativeMethods.ENABLE_ECHO_INPUT);
        }
    }

    [Fact]
    public void VT_input_stays_off_because_we_read_INPUT_RECORDs()
    {
        uint mode = ConsoleHost.InputMode(Typical | NativeMethods.ENABLE_VIRTUAL_TERMINAL_INPUT, true);
        Assert.Equal(0u, mode & NativeMethods.ENABLE_VIRTUAL_TERMINAL_INPUT);
    }

    [Fact]
    public void Mouse_on_takes_quick_edit_away_and_sets_extended_flags_in_the_same_word()
    {
        uint mode = ConsoleHost.InputMode(Typical, enableMouse: true);

        Assert.Equal(NativeMethods.ENABLE_MOUSE_INPUT, mode & NativeMethods.ENABLE_MOUSE_INPUT);
        Assert.Equal(0u, mode & NativeMethods.ENABLE_QUICK_EDIT_MODE);
        // Without this in the same SetConsoleMode call the clear above is ignored,
        // silently, and there is no mouse input and no error.
        Assert.Equal(NativeMethods.ENABLE_EXTENDED_FLAGS, mode & NativeMethods.ENABLE_EXTENDED_FLAGS);
    }

    [Fact]
    public void Mouse_off_leaves_quick_edit_exactly_as_it_found_it()
    {
        // This is what makes `mouse = false` in config.toml worth having today: the
        // editor does not consume mouse events yet, so the observable difference is
        // whether the terminal keeps its own drag-select-and-copy.
        uint on = ConsoleHost.InputMode(Typical, enableMouse: false);
        Assert.Equal(NativeMethods.ENABLE_QUICK_EDIT_MODE, on & NativeMethods.ENABLE_QUICK_EDIT_MODE);
        Assert.Equal(0u, on & NativeMethods.ENABLE_MOUSE_INPUT);

        uint off = ConsoleHost.InputMode(Typical & ~NativeMethods.ENABLE_QUICK_EDIT_MODE, false);
        Assert.Equal(0u, off & NativeMethods.ENABLE_QUICK_EDIT_MODE);
    }

    [Fact]
    public void Window_input_is_always_on_because_resize_arrives_through_it()
    {
        uint mode = ConsoleHost.InputMode(0, enableMouse: false);
        Assert.Equal(NativeMethods.ENABLE_WINDOW_INPUT, mode & NativeMethods.ENABLE_WINDOW_INPUT);
    }
}
