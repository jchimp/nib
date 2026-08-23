namespace Nib.Terminal;

/// <summary>A 24-bit colour, or the sentinel "terminal default" (emitted as ESC[39m/ESC[49m).</summary>
public readonly record struct Color(byte R, byte G, byte B, bool IsDefault)
{
    /// <summary>Whatever the terminal's own foreground/background is. The only colour phase 2 uses.</summary>
    public static readonly Color Default = new(0, 0, 0, true);

    public static Color Rgb(byte r, byte g, byte b) => new(r, g, b, false);
}

/// <summary>One screen cell. Value equality drives the diff, so this is a record struct.</summary>
public readonly record struct Cell(char Ch, Color Fg, Color Bg)
{
    public static readonly Cell Blank = new(' ', Color.Default, Color.Default);
}

/// <summary>
/// A double-buffered character grid. <see cref="EditorView"/> (or any drawer)
/// paints into the back grid each frame; <see cref="Render"/> diffs it against the
/// front grid and emits only the cells that changed, as minimal cursor-move + SGR
/// runs, into a <see cref="TerminalWriter"/>. One flush per frame is the caller's
/// job — this never flushes.
///
/// The diff is what keeps a scroll cheap: a one-line scroll on a full screen
/// touches every row, but a cursor blink or a status-bar tick touches almost
/// nothing, and the byte count out the door tracks the change, not the screen.
/// </summary>
public sealed class Screen
{
    private Cell[] _back;
    private Cell[] _front;
    // Forces the next Render to treat every cell as changed. Set on construction
    // and on resize, when the front grid can't be trusted as a baseline.
    private bool _fullRepaint;

    // The style we last emitted, tracked ACROSS frames because the terminal keeps
    // its SGR state between writes. null means "unknown" (nothing emitted yet), so
    // the first cell of the first frame always sets an explicit colour. Persisting
    // this is what lets an idle frame — nothing changed — emit literally nothing,
    // and it holds only because Screen is the sole thing writing SGR in phase 2.
    private Color? _emittedFg;
    private Color? _emittedBg;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public Screen(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _back = NewGrid(Width * Height);
        _front = NewGrid(Width * Height);
        _fullRepaint = true;
    }

    private static Cell[] NewGrid(int size)
    {
        var grid = new Cell[size];
        Array.Fill(grid, Cell.Blank);
        return grid;
    }

    /// <summary>Reset the back grid before painting a frame.</summary>
    public void Clear(Cell blank) => Array.Fill(_back, blank, 0, Width * Height);

    public void Set(int x, int y, char ch, Color fg, Color bg)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        _back[y * Width + x] = new Cell(ch, fg, bg);
    }

    /// <summary>
    /// The back grid's cell at (x,y). A test affordance, like TerminalWriter's
    /// snapshot — it lets a renderer test assert on what was painted without a
    /// console. Internal on purpose: nothing in the editor reads the grid back.
    /// </summary>
    internal Cell CellAt(int x, int y) =>
        (uint)x >= (uint)Width || (uint)y >= (uint)Height ? Cell.Blank : _back[y * Width + x];

    /// <summary>Write a run left-to-right from (x,y), clipping at the right edge. Off-grid rows are dropped.</summary>
    public void PutText(int x, int y, ReadOnlySpan<char> text, Color fg, Color bg)
    {
        if ((uint)y >= (uint)Height) return;
        int row = y * Width;
        for (int i = 0; i < text.Length; i++)
        {
            int cx = x + i;
            if (cx < 0) continue;
            if (cx >= Width) break;
            _back[row + cx] = new Cell(text[i], fg, bg);
        }
    }

    /// <summary>
    /// Reallocate to a new size and force a full repaint. The caller must repaint
    /// the back grid (clear + draw) before the next <see cref="Render"/> — the
    /// reallocated grids hold no meaningful content.
    /// </summary>
    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        int size = Width * Height;
        if (_back.Length != size)
        {
            _back = NewGrid(size);
            _front = NewGrid(size);
        }
        _fullRepaint = true;
    }

    /// <summary>
    /// Diff back against front and emit the minimal VT to reconcile them. Does not
    /// flush. After emitting, the back grid becomes the new front (reference swap).
    /// </summary>
    public void Render(TerminalWriter o)
    {
        for (int y = 0; y < Height; y++)
        {
            int rowBase = y * Width;
            int x = 0;
            while (x < Width)
            {
                int idx = rowBase + x;
                if (!_fullRepaint && _back[idx] == _front[idx]) { x++; continue; }

                // One cursor move per contiguous run of changed cells; unchanged
                // cells between runs emit nothing and the persisted SGR carries
                // across the gap.
                o.MoveTo(y + 1, x + 1);
                while (x < Width)
                {
                    idx = rowBase + x;
                    if (!_fullRepaint && _back[idx] == _front[idx]) break;

                    Cell c = _back[idx];
                    if (_emittedFg != c.Fg) { EmitFg(o, c.Fg); _emittedFg = c.Fg; }
                    if (_emittedBg != c.Bg) { EmitBg(o, c.Bg); _emittedBg = c.Bg; }
                    o.Write(c.Ch == '\0' ? ' ' : c.Ch);
                    _front[idx] = c;
                    x++;
                }
            }
        }

        _fullRepaint = false;
    }

    private static void EmitFg(TerminalWriter o, Color c)
    {
        if (c.IsDefault) o.ForegroundDefault();
        else o.Foreground(c.R, c.G, c.B);
    }

    private static void EmitBg(TerminalWriter o, Color c)
    {
        if (c.IsDefault) o.BackgroundDefault();
        else o.Background(c.R, c.G, c.B);
    }
}
