using Nib.Terminal;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// <see cref="InputReader.DecodeMouse"/> against hand-built MOUSE_EVENT_RECORDs.
/// ReadBatch needs a live console; the decode is the part with arithmetic in it.
/// </summary>
public class MouseDecodeTests
{
    private static InputEvent Decode(uint buttons, uint flags, short x = 0, short y = 0, uint ctrl = 0)
    {
        var rec = new NativeMethods.MOUSE_EVENT_RECORD
        {
            dwMousePosition = new NativeMethods.COORD { X = x, Y = y },
            dwButtonState = buttons,
            dwControlKeyState = ctrl,
            dwEventFlags = flags,
        };
        return InputReader.DecodeMouse(in rec);
    }

    [Fact]
    public void Left_press_is_ButtonDown_with_Left_and_the_cell()
    {
        InputEvent ev = Decode(NativeMethods.FROM_LEFT_1ST_BUTTON_PRESSED, 0, x: 12, y: 3);
        Assert.Equal(InputEventKind.Mouse, ev.Kind);
        Assert.Equal(MouseAction.ButtonDown, ev.MouseAction);
        Assert.Equal(MouseButtons.Left, ev.Buttons);
        Assert.Equal(12, ev.MouseX);
        Assert.Equal(3, ev.MouseY);
    }

    [Fact]
    public void Release_is_ButtonUp_with_no_buttons()
    {
        InputEvent ev = Decode(0, 0);
        Assert.Equal(MouseAction.ButtonUp, ev.MouseAction);
        Assert.Equal(MouseButtons.None, ev.Buttons);
    }

    // The whole reason Buttons rides on every event: Windows reports a drag as
    // MOUSE_MOVED with the press bit still set, never as a repeated press.
    [Fact]
    public void Drag_is_Move_with_Left_still_held()
    {
        InputEvent ev = Decode(NativeMethods.FROM_LEFT_1ST_BUTTON_PRESSED, NativeMethods.MOUSE_MOVED);
        Assert.Equal(MouseAction.Move, ev.MouseAction);
        Assert.Equal(MouseButtons.Left, ev.Buttons);
    }

    [Fact]
    public void Hover_is_Move_with_nothing_held()
    {
        InputEvent ev = Decode(0, NativeMethods.MOUSE_MOVED);
        Assert.Equal(MouseAction.Move, ev.MouseAction);
        Assert.Equal(MouseButtons.None, ev.Buttons);
    }

    [Fact]
    public void Right_and_middle_are_told_apart()
    {
        Assert.Equal(MouseButtons.Right, Decode(NativeMethods.RIGHTMOST_BUTTON_PRESSED, 0).Buttons);
        Assert.Equal(MouseButtons.Middle, Decode(NativeMethods.FROM_LEFT_2ND_BUTTON_PRESSED, 0).Buttons);
    }

    [Theory]
    [InlineData(120, 1)]
    [InlineData(-120, -1)]
    [InlineData(240, 2)]
    [InlineData(-360, -3)]
    public void Wheel_delta_is_signed_detents(short raw, int detents)
    {
        // The delta is the signed high word of dwButtonState.
        uint state = (uint)((ushort)raw) << 16;
        InputEvent ev = Decode(state, NativeMethods.MOUSE_WHEELED);
        Assert.Equal(MouseAction.Wheel, ev.MouseAction);
        Assert.Equal(detents, ev.WheelDelta);
    }

    [Fact]
    public void Double_click_wins_over_a_moved_flag()
    {
        InputEvent ev = Decode(NativeMethods.FROM_LEFT_1ST_BUTTON_PRESSED,
            NativeMethods.DOUBLE_CLICK | NativeMethods.MOUSE_MOVED);
        Assert.Equal(MouseAction.DoubleClick, ev.MouseAction);
        Assert.Equal(MouseButtons.Left, ev.Buttons);
    }

    [Fact]
    public void Modifiers_come_through()
    {
        InputEvent ev = Decode(NativeMethods.FROM_LEFT_1ST_BUTTON_PRESSED, 0,
            ctrl: NativeMethods.SHIFT_PRESSED | NativeMethods.LEFT_CTRL_PRESSED);
        Assert.True(ev.Shift);
        Assert.True(ev.Ctrl);
        Assert.False(ev.Alt);
    }
}
