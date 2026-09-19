using Nib.Commands;
using Nib.Model;
using Nib.Terminal;

namespace Nib.Ui;

/// <summary>
/// The mouse counterpart of <see cref="Keymap"/>: turns decoded mouse events into
/// caret, selection and viewport changes. Lives in <c>Ui/</c> rather than
/// <c>Commands/</c> because it needs <see cref="EditorView"/> for the screen
/// geometry as well as <see cref="EditorCommands"/> for the edits, and
/// <c>Commands/</c> does not reference <c>Ui/</c>.
///
/// Every gesture that moves the caret goes through <see cref="EditorCommands.Move"/>
/// with the same <c>extend</c> flag Shift+arrow uses, so a mouse selection is a
/// keyboard selection by construction rather than a second implementation of one.
///
/// Console-free: takes events, never handles, so it is tested without a terminal.
/// </summary>
public sealed class MouseHandler
{
    /// <summary>Lines scrolled per wheel detent. Windows' own default.</summary>
    public const int WheelLines = 3;

    private readonly EditorView _view;
    private readonly Viewport _viewport;
    private readonly EditorCommands _cmd;

    public MouseHandler(EditorView view, Viewport viewport, EditorCommands cmd)
    {
        _view = view;
        _viewport = viewport;
        _cmd = cmd;
    }

    /// <summary>
    /// True between a left press in the text area and its release. Needed because a
    /// drag arrives as Move events with the button bit set, and a Move with Left held
    /// that did not start on text (a press on the title row, say) must not select.
    /// </summary>
    public bool Dragging { get; private set; }

    /// <summary>
    /// A modal prompt that swallowed the button-up would otherwise leave
    /// <see cref="Dragging"/> stuck on, and the next hover would select.
    /// </summary>
    public void CancelDrag() => Dragging = false;

    /// <summary>
    /// Handle one mouse event. Returns true when the event moved the caret or used
    /// it (click, drag, double-click, paste) — the caller uses that to decide whether
    /// the view should follow the caret again after a wheel scroll parted them.
    /// </summary>
    public bool Handle(InputEvent ev, int lineCount)
    {
        if (ev.Kind != InputEventKind.Mouse) return false;

        switch (ev.MouseAction)
        {
            case MouseAction.Wheel:
                // Viewport only. The caret stays where it is, off-screen if need be,
                // and the next keystroke brings the view back to it — scrolling to
                // look is not a request to move, and it must never grow a selection.
                _viewport.ScrollLines(-ev.WheelDelta * WheelLines);
                _viewport.ClampVertical(lineCount);
                return false;

            case MouseAction.ButtonDown when ev.Buttons == MouseButtons.Left:
            {
                if (_view.HitTest(ev.MouseX, ev.MouseY) is not { } hit) return false;
                // Shift+click is Shift+arrow aimed at a cell: extend from the
                // existing anchor, or from the caret if there is none.
                _cmd.Move(() => _cmd.Cursor.MoveTo(hit.Row, hit.Col), extend: ev.Shift);
                Dragging = true;
                return true;
            }

            case MouseAction.Move when Dragging && (ev.Buttons & MouseButtons.Left) != 0:
            {
                // Clamped, not null-checked: a drag that runs past the top or bottom
                // edge should keep selecting toward that edge.
                TextPosition hit = _view.HitTestClamped(ev.MouseX, ev.MouseY);
                _cmd.Move(() => _cmd.Cursor.MoveTo(hit.Row, hit.Col), extend: true);
                return true;
            }

            case MouseAction.ButtonUp:
                // Nothing else to do: a click that never moved leaves an empty
                // anchor, and Selection.IsActive already treats empty as none.
                Dragging = false;
                return false;

            case MouseAction.DoubleClick when ev.Buttons == MouseButtons.Left:
            {
                if (_view.HitTest(ev.MouseX, ev.MouseY) is not { } hit) return false;
                _cmd.SelectWordAt(hit);
                return true;
            }

            case MouseAction.ButtonDown when ev.Buttons == MouseButtons.Right:
                // Windows Terminal's convention. Deliberately does not move the caret
                // to the pointer first: the paste lands where the caret is, which is
                // where the user was looking, not wherever the pointer happened to be.
                _cmd.Paste();
                return true;

            default:
                return false;
        }
    }
}
