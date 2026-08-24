using Nib.Model;
using Nib.Terminal;
using Nib.Ui;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The message row's styling. It used to paint in Color.Default on Color.Default,
/// which is exactly the body text's styling — so "Wrote 42 lines" and
/// "Error: access denied" were indistinguishable from each other and from the file.
/// </summary>
public class MessageRowTests
{
    private const int Width = 80;
    private const int Height = 24;
    private const int MessageRow = Height - 3;

    private static (EditorView View, Screen Screen) Setup()
    {
        var buffer = new TextBuffer([new Line("alpha", LineEnding.None)], DocumentEncoding.Utf8NoBom, null);
        var screen = new Screen(Width, Height);
        var viewport = new Viewport();
        var cursor = new Cursor(buffer, viewport.TabWidth);
        return (new EditorView(screen, viewport, buffer, cursor), screen);
    }

    private static string RowText(Screen screen, int length) =>
        new([.. Enumerable.Range(0, length).Select(i => screen.CellAt(i, MessageRow).Ch)]);

    [Fact]
    public void An_empty_message_paints_nothing_at_all()
    {
        (EditorView view, Screen screen) = Setup();
        view.Render();

        Cell cell = screen.CellAt(0, MessageRow);
        Assert.Equal(' ', cell.Ch);
        Assert.Equal(Cell.Blank.Bg, cell.Bg); // no background where there is no message
    }

    [Fact]
    public void An_info_message_is_padded_by_one_space_on_each_side()
    {
        (EditorView view, Screen screen) = Setup();
        view.Message = "Wrote 42 lines";
        view.Render();

        Assert.Equal(" Wrote 42 lines ", RowText(screen, 16));

        // The pad carries the background; the cell past it does not.
        Assert.Equal(screen.CellAt(1, MessageRow).Bg, screen.CellAt(0, MessageRow).Bg);
        Assert.NotEqual(screen.CellAt(1, MessageRow).Bg, screen.CellAt(16, MessageRow).Bg);
    }

    [Fact]
    public void Info_error_and_prompt_are_three_distinguishable_styles()
    {
        (EditorView view, Screen screen) = Setup();

        view.SetMessage("x", MessageKind.Info);
        view.Render();
        Cell info = screen.CellAt(1, MessageRow);

        view.SetMessage("x", MessageKind.Error);
        view.Render();
        Cell error = screen.CellAt(1, MessageRow);

        view.SetMessage("x", MessageKind.Prompt);
        view.Render();
        Cell prompt = screen.CellAt(1, MessageRow);

        Assert.NotEqual(info.Bg, error.Bg);
        Assert.NotEqual(info.Bg, prompt.Bg);
        Assert.NotEqual(error.Bg, prompt.Bg);

        // And none of them is the body text's styling, which is the whole point.
        Assert.NotEqual(Color.Default, info.Bg);
        Assert.NotEqual(Color.Default, info.Fg);
    }

    [Fact]
    public void Assigning_Message_clears_a_previous_error_styling()
    {
        (EditorView view, Screen screen) = Setup();

        view.SetMessage("Error: access denied", MessageKind.Error);
        view.Render();
        Color errorBg = screen.CellAt(1, MessageRow).Bg;

        // A plain assignment is Info. Without the reset, the next success would
        // inherit the failure's red.
        view.Message = "Wrote 42 lines";
        Assert.Equal(MessageKind.Info, view.MessageKind);

        view.Render();
        Assert.NotEqual(errorBg, screen.CellAt(1, MessageRow).Bg);
    }

    [Fact]
    public void An_active_prompt_is_styled_as_a_prompt_whatever_the_message_kind_was()
    {
        (EditorView view, Screen screen) = Setup();
        view.SetMessage("Error: access denied", MessageKind.Error);
        view.Render();
        Color errorBg = screen.CellAt(1, MessageRow).Bg;

        view.ActivePrompt = new Prompt("Save as: ");
        view.Render();

        Assert.Equal(" Save as: ", RowText(screen, 10));
        Assert.NotEqual(errorBg, screen.CellAt(1, MessageRow).Bg);
    }

    [Fact]
    public void The_prompt_caret_clears_the_pad()
    {
        (EditorView view, _) = Setup();
        var prompt = new Prompt("Save as: ");
        view.ActivePrompt = prompt;

        view.Render();
        Assert.Equal(MessageRow, view.CursorY);
        Assert.Equal(1 + "Save as: ".Length, view.CursorX); // 1 = the leading pad

        prompt.InsertText("nib.conf");
        view.Render();
        Assert.Equal(1 + "Save as: ".Length + "nib.conf".Length, view.CursorX);
    }

    [Fact]
    public void A_message_wider_than_the_screen_is_clipped_not_wrapped()
    {
        (EditorView view, Screen screen) = Setup();
        view.Message = new string('m', Width * 2);
        view.Render();

        Assert.Equal('m', screen.CellAt(Width - 1, MessageRow).Ch);
        // The row below it is a help bar and must be untouched.
        Assert.Equal('^', screen.CellAt(0, Height - 2).Ch);
    }

    // ---- the find prompt's status run ------------------------------------------

    [Fact]
    public void A_prompt_status_paints_after_the_input()
    {
        // ^F stays open across matches, and ActivePrompt displaces Message while it is
        // up — so without this run a search has nowhere to say "wrapped" until the
        // prompt closes and the answer is stale.
        (EditorView view, Screen screen) = Setup();
        var prompt = new Prompt("Search: ", "beta");
        prompt.SetStatus("Search wrapped");
        view.ActivePrompt = prompt;
        view.Render();

        Assert.Equal(" Search: beta  Search wrapped ", RowText(screen, 30));
    }

    [Fact]
    public void A_prompt_status_keeps_its_own_colour()
    {
        (EditorView view, Screen screen) = Setup();
        var prompt = new Prompt("Search: ", "zzz");
        prompt.SetStatus("Not found: zzz", MessageKind.Error);
        view.ActivePrompt = prompt;
        view.Render();

        Color promptBg = screen.CellAt(1, MessageRow).Bg;              // inside the label
        Color statusBg = screen.CellAt(" Search: zzz  ".Length, MessageRow).Bg; // inside the status

        Assert.NotEqual(promptBg, statusBg); // an error must not read as a plain prompt
    }

    [Fact]
    public void A_prompt_with_no_status_paints_exactly_as_before()
    {
        (EditorView view, Screen screen) = Setup();
        view.ActivePrompt = new Prompt("Save as: ", "nib.conf");
        view.Render();

        Assert.Equal(" Save as: nib.conf ", RowText(screen, 19));
        Assert.Equal(Cell.Blank.Bg, screen.CellAt(19, MessageRow).Bg); // and nothing past it
    }

    [Fact]
    public void A_long_status_is_clipped_rather_than_overflowing_the_row()
    {
        (EditorView view, Screen screen) = Setup();
        var prompt = new Prompt("Search: ", new string('x', 40));
        prompt.SetStatus("Not found: " + new string('x', 40), MessageKind.Error);
        view.ActivePrompt = prompt;
        view.Render();

        // The status is cut short of the last cell so the closing pad still fits: a run
        // clipped flush to the edge reads as truncated even when it is not.
        Cell last = screen.CellAt(Width - 1, MessageRow);
        Assert.Equal(' ', last.Ch);
        Assert.Equal(screen.CellAt(1, MessageRow).Bg, last.Bg); // the prompt's own colour, not the status's
    }

    [Fact]
    public void The_caret_ignores_the_status_run()
    {
        // The status is appended after the input, so it must not shift the caret --
        // which is placed off Label.Length + Caret.
        (EditorView view, Screen screen) = Setup();
        var prompt = new Prompt("Search: ", "beta");
        view.Render();
        view.ActivePrompt = prompt;
        view.Render();
        int before = view.CursorX;

        prompt.SetStatus("Search wrapped");
        view.Render();

        Assert.Equal(before, view.CursorX);
        Assert.Equal(1 + "Search: ".Length + "beta".Length, view.CursorX);
    }
}
