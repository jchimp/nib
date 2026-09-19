using Nib.Commands;
using Nib.Model;
using Nib.Terminal;
using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// <see cref="MouseHandler"/> over a real view, viewport and command set, with
/// events built the way <c>KeymapTests</c> builds keystrokes. The load-bearing
/// assertion is that a mouse selection is the keyboard selection: same anchor,
/// same range, same clearing rules.
/// </summary>
public class MouseHandlerTests
{
    private const int Width = 80;
    private const int Height = 24; // 20 text rows

    private sealed class FakeClipboard : IClipboard
    {
        public string Text = "";
        public string GetText() => Text;
        public bool SetText(string text) { Text = text; return true; }
    }

    private sealed record Rig(
        MouseHandler Mouse, EditorView View, Viewport Viewport, EditorCommands Cmd,
        TextBuffer Buffer, Cursor Cursor, FakeClipboard Clip);

    private static Rig Setup(params string[] lines)
    {
        if (lines.Length == 0) lines = ["alpha beta", "gamma", "delta"];
        var list = new List<Line>();
        for (int i = 0; i < lines.Length; i++)
            list.Add(new Line(lines[i], i == lines.Length - 1 ? LineEnding.None : LineEnding.Lf));
        var buffer = new TextBuffer(list, DocumentEncoding.Utf8NoBom, null);
        var screen = new Screen(Width, Height);
        var viewport = new Viewport();
        var cursor = new Cursor(buffer, viewport.TabWidth);
        var view = new EditorView(screen, viewport, buffer, cursor);
        var clip = new FakeClipboard();
        var cmd = new EditorCommands(buffer, cursor, clip);
        view.Selection = cmd.Selection;
        return new Rig(new MouseHandler(view, viewport, cmd), view, viewport, cmd, buffer, cursor, clip);
    }

    private static string[] Lines(int count, string text = "line")
    {
        var lines = new string[count];
        for (int i = 0; i < count; i++) lines[i] = $"{text} {i}";
        return lines;
    }

    private static InputEvent Mouse(MouseAction action, int x, int y,
        MouseButtons buttons = MouseButtons.None, KeyModifiers mods = KeyModifiers.None, int wheel = 0) =>
        new()
        {
            Kind = InputEventKind.Mouse, MouseAction = action, MouseX = x, MouseY = y,
            Buttons = buttons, Modifiers = mods, WheelDelta = wheel,
        };

    private static InputEvent Down(int x, int y, KeyModifiers mods = KeyModifiers.None) =>
        Mouse(MouseAction.ButtonDown, x, y, MouseButtons.Left, mods);
    private static InputEvent Drag(int x, int y) => Mouse(MouseAction.Move, x, y, MouseButtons.Left);
    private static InputEvent Up(int x, int y) => Mouse(MouseAction.ButtonUp, x, y);
    private static InputEvent Wheel(int detents) => Mouse(MouseAction.Wheel, 0, 5, wheel: detents);

    private static bool Send(Rig r, InputEvent ev) => r.Mouse.Handle(ev, r.Buffer.LineCount);

    // ---- click ---------------------------------------------------------------

    [Fact]
    public void Click_places_the_caret_and_clears_any_selection()
    {
        Rig r = Setup();
        r.Cmd.SelectAll();
        Assert.True(r.Cmd.HasSelection);

        Assert.True(Send(r, Down(3, 2)));
        Assert.Equal((1, 3), (r.Cursor.Row, r.Cursor.Col));
        Assert.False(r.Cmd.HasSelection);
        Assert.True(r.Mouse.Dragging);

        Send(r, Up(3, 2));
        Assert.False(r.Mouse.Dragging);
        Assert.False(r.Cmd.HasSelection); // a click that never moved selects nothing
    }

    [Fact]
    public void Click_on_the_title_row_does_nothing_and_starts_no_drag()
    {
        Rig r = Setup();
        r.Cursor.MoveTo(2, 1);
        Assert.False(Send(r, Down(3, 0)));
        Assert.Equal((2, 1), (r.Cursor.Row, r.Cursor.Col));
        Assert.False(r.Mouse.Dragging);

        // A Move with Left held after a press that was not on text must not select.
        Assert.False(Send(r, Drag(3, 2)));
        Assert.False(r.Cmd.HasSelection);
    }

    [Fact]
    public void Shift_click_extends_from_the_caret_like_shift_arrow()
    {
        Rig r = Setup();
        r.Cursor.MoveTo(0, 2);
        Send(r, Down(4, 2, KeyModifiers.Shift));

        Assert.Equal((new TextPosition(0, 2), new TextPosition(1, 4)), r.Cmd.Selection.Range(r.Cursor));
    }

    // ---- drag ----------------------------------------------------------------

    [Fact]
    public void Drag_selects_exactly_what_the_keyboard_would()
    {
        Rig mouse = Setup();
        Send(mouse, Down(2, 1));
        Send(mouse, Drag(3, 2));
        Send(mouse, Drag(4, 3));
        Send(mouse, Up(4, 3));

        Rig keys = Setup();
        keys.Cursor.MoveTo(0, 2);
        keys.Cmd.Move(keys.Cursor.Down, extend: true);
        keys.Cmd.Move(keys.Cursor.Down, extend: true);
        keys.Cmd.Move(keys.Cursor.Right, extend: true);
        keys.Cmd.Move(keys.Cursor.Right, extend: true);

        Assert.Equal(keys.Cmd.Selection.Range(keys.Cursor), mouse.Cmd.Selection.Range(mouse.Cursor));
        Assert.Equal((2, 4), (mouse.Cursor.Row, mouse.Cursor.Col));
        Assert.Equal(keys.Cmd.Selection.Anchor, mouse.Cmd.Selection.Anchor);
    }

    [Fact]
    public void Drag_backwards_normalises_like_the_keyboard()
    {
        Rig r = Setup();
        Send(r, Down(3, 3));
        Send(r, Drag(1, 1));
        Assert.Equal((new TextPosition(0, 1), new TextPosition(2, 3)), r.Cmd.Selection.Range(r.Cursor));
    }

    [Fact]
    public void Drag_past_the_bottom_edge_keeps_selecting_toward_it()
    {
        Rig r = Setup(Lines(50));
        Send(r, Down(0, 1));
        Send(r, Drag(0, 23)); // help row, below the text area
        Assert.Equal(19, r.Cursor.Row); // last visible text row
        Assert.True(r.Cmd.HasSelection);
    }

    [Fact]
    public void A_hover_with_no_button_never_selects()
    {
        Rig r = Setup();
        r.Cursor.MoveTo(0, 0);
        Assert.False(Send(r, Mouse(MouseAction.Move, 5, 3)));
        Assert.Equal((0, 0), (r.Cursor.Row, r.Cursor.Col));
        Assert.False(r.Cmd.HasSelection);
    }

    [Fact]
    public void Cancel_drag_stops_a_release_lost_to_a_prompt_from_selecting_on_the_next_hover()
    {
        Rig r = Setup();
        Send(r, Down(0, 1));
        r.Mouse.CancelDrag();
        Assert.False(r.Mouse.Dragging);
        Assert.False(Send(r, Drag(3, 3)));
        Assert.False(r.Cmd.HasSelection);
    }

    [Fact]
    public void Drag_then_copy_puts_the_text_on_the_clipboard()
    {
        Rig r = Setup("alpha beta", "gamma");
        Send(r, Down(0, 1));
        Send(r, Drag(5, 1));
        Send(r, Up(5, 1));
        r.Cmd.Copy();
        Assert.Equal("alpha", r.Clip.Text);
    }

    // ---- wheel ---------------------------------------------------------------

    [Fact]
    public void Wheel_scrolls_the_viewport_three_lines_per_detent_and_leaves_the_caret_alone()
    {
        Rig r = Setup(Lines(100));
        r.Cursor.MoveTo(0, 2);

        Assert.False(Send(r, Wheel(-1))); // wheel down: toward the end
        Assert.Equal(MouseHandler.WheelLines, r.Viewport.FirstLine);
        Assert.Equal((0, 2), (r.Cursor.Row, r.Cursor.Col));

        Send(r, Wheel(-2));
        Assert.Equal(3 * MouseHandler.WheelLines, r.Viewport.FirstLine);

        Send(r, Wheel(1));
        Assert.Equal(2 * MouseHandler.WheelLines, r.Viewport.FirstLine);
    }

    [Fact]
    public void Wheel_clamps_at_the_top_and_at_the_last_line()
    {
        Rig r = Setup(Lines(10));
        Send(r, Wheel(5));
        Assert.Equal(0, r.Viewport.FirstLine);
        Send(r, Wheel(-100));
        Assert.Equal(9, r.Viewport.FirstLine); // nano lets the last line reach the top
    }

    [Fact]
    public void Wheel_never_touches_the_selection()
    {
        Rig r = Setup(Lines(100));
        Send(r, Down(0, 1));
        Send(r, Drag(3, 2));
        Send(r, Up(3, 2));
        var before = r.Cmd.Selection.Range(r.Cursor);

        Send(r, Wheel(-10));
        Assert.Equal(before, r.Cmd.Selection.Range(r.Cursor));
    }

    // ---- double-click ----------------------------------------------------------

    [Fact]
    public void Double_click_selects_the_whitespace_delimited_word()
    {
        Rig r = Setup("foo, bar_baz qux");
        Assert.True(Send(r, Mouse(MouseAction.DoubleClick, 7, 1, MouseButtons.Left)));
        Assert.Equal((new TextPosition(0, 5), new TextPosition(0, 12)), r.Cmd.Selection.Range(r.Cursor));
        Assert.Equal((0, 12), (r.Cursor.Row, r.Cursor.Col));

        // Same rule as ^arrow: punctuation is part of the word.
        Send(r, Mouse(MouseAction.DoubleClick, 1, 1, MouseButtons.Left));
        Assert.Equal((new TextPosition(0, 0), new TextPosition(0, 4)), r.Cmd.Selection.Range(r.Cursor));
    }

    [Fact]
    public void Double_click_on_whitespace_or_past_the_end_selects_nothing()
    {
        Rig r = Setup("foo bar");
        r.Cmd.SelectAll();
        Send(r, Mouse(MouseAction.DoubleClick, 3, 1, MouseButtons.Left));
        Assert.False(r.Cmd.HasSelection);
        Assert.Equal((0, 3), (r.Cursor.Row, r.Cursor.Col));

        Send(r, Mouse(MouseAction.DoubleClick, 40, 1, MouseButtons.Left));
        Assert.False(r.Cmd.HasSelection);
        Assert.Equal((0, 7), (r.Cursor.Row, r.Cursor.Col));
    }

    // ---- right-click ---------------------------------------------------------

    [Fact]
    public void Right_click_pastes_at_the_caret_not_the_pointer()
    {
        Rig r = Setup("abc", "def");
        r.Clip.Text = "XY";
        r.Cursor.MoveTo(1, 1);
        Assert.True(Send(r, Mouse(MouseAction.ButtonDown, 0, 1, MouseButtons.Right)));
        Assert.Equal("dXYef", r.Buffer.GetLine(1));
        Assert.Equal("abc", r.Buffer.GetLine(0));
    }

    [Fact]
    public void Right_click_pastes_over_the_selection()
    {
        Rig r = Setup("alpha beta");
        r.Clip.Text = "Z";
        Send(r, Down(0, 1));
        Send(r, Drag(5, 1));
        Send(r, Up(5, 1));
        Send(r, Mouse(MouseAction.ButtonDown, 2, 1, MouseButtons.Right));
        Assert.Equal("Z beta", r.Buffer.GetLine(0));
    }

    [Fact]
    public void Middle_button_and_key_events_are_ignored()
    {
        Rig r = Setup();
        Assert.False(Send(r, Mouse(MouseAction.ButtonDown, 2, 1, MouseButtons.Middle)));
        Assert.False(Send(r, new InputEvent { Kind = InputEventKind.Key, Key = ConsoleKey.A }));
        Assert.Equal((0, 0), (r.Cursor.Row, r.Cursor.Col));
    }
}
