namespace Nib.Model;

/// <summary>
/// The whole file in memory as a <see cref="List{Line}"/>, plus the encoding and
/// path needed to write it back. All editing goes through here; the console
/// layers only read. Console-free by rule — this is the code the byte-for-byte
/// round-trip and undo tests exercise with no terminal attached.
///
/// Positions are (row, char-column) where the column is a UTF-16 index into the
/// line's <see cref="Line.Text"/>, matching <see cref="TabStops"/>. Callers clamp
/// their own cursors; the ops here assume in-range positions.
/// </summary>
public sealed class TextBuffer
{
    private readonly List<Line> _lines;

    public DocumentEncoding Encoding { get; }

    /// <summary>The ending given to line breaks the user creates (Enter). The file's dominant ending, or LF.</summary>
    public LineEnding DefaultEnding { get; }

    /// <summary>Full path last loaded from or saved to; null for a new buffer.</summary>
    public string? Path { get; set; }

    public bool IsModified { get; private set; }

    public TextBuffer(List<Line> lines, DocumentEncoding encoding, string? path)
    {
        // A buffer always has at least one line so the cursor and renderer have
        // somewhere to stand.
        _lines = lines.Count == 0 ? [new Line("", LineEnding.None)] : lines;
        Encoding = encoding;
        Path = path;
        DefaultEnding = DominantEnding(_lines);
    }

    /// <summary>An empty, unsaved buffer: one blank line, UTF-8, no BOM.</summary>
    public static TextBuffer Empty(string? path = null) =>
        new([new Line("", LineEnding.None)], DocumentEncoding.Utf8NoBom, path);

    // ---- read surface (used by the renderer) --------------------------------

    public int LineCount => _lines.Count;
    public string GetLine(int index) => _lines[index].Text;
    public Line LineAt(int index) => _lines[index];
    public int LineLength(int index) => _lines[index].Text.Length;
    public string? Name => Path is null ? null : System.IO.Path.GetFileName(Path);

    /// <summary>The reconstructed text of the whole buffer, terminators included. Save encodes this.</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (Line line in _lines)
        {
            sb.Append(line.Text);
            sb.Append(line.Ending.ToChars());
        }
        return sb.ToString();
    }

    // ---- edits --------------------------------------------------------------
    // Each mutation marks the buffer modified. Positions are assumed valid; the
    // command layer clamps the cursor before calling.

    public void InsertChar(int row, int col, char ch) => InsertText(row, col, ch.ToString());

    /// <summary>Insert text with no embedded newline (a char, or an astral surrogate pair).</summary>
    public void InsertText(int row, int col, string text)
    {
        if (text.Length == 0) return;
        Line line = _lines[row];
        line.Text = line.Text.Insert(col, text);
        IsModified = true;
    }

    /// <summary>
    /// Break <paramref name="row"/> at <paramref name="col"/>. The tail moves to a
    /// new line that keeps the original terminator; the head takes a fresh
    /// <see cref="DefaultEnding"/> — the line break the user just made.
    /// </summary>
    public void SplitLine(int row, int col)
    {
        Line line = _lines[row];
        string head = line.Text[..col];
        string tail = line.Text[col..];

        var tailLine = new Line(tail, line.Ending);
        line.Text = head;
        line.Ending = DefaultEnding;
        _lines.Insert(row + 1, tailLine);
        IsModified = true;
    }

    /// <summary>
    /// Backspace at (row, col). Inside a line, removes the char before the caret.
    /// At col 0 with a previous line, joins onto the previous line (the previous
    /// line's terminator — the break being deleted — disappears). No-op at (0, 0).
    /// </summary>
    public void DeleteBackward(int row, int col)
    {
        if (col > 0)
        {
            Line line = _lines[row];
            line.Text = line.Text.Remove(col - 1, 1);
            IsModified = true;
        }
        else if (row > 0)
        {
            JoinWithNext(row - 1);
        }
    }

    /// <summary>
    /// Delete at (row, col). Inside a line, removes the char at the caret. At end
    /// of line with a following line, joins the next line up (this line's
    /// terminator disappears). No-op at end of the last line.
    /// </summary>
    public void DeleteForward(int row, int col)
    {
        Line line = _lines[row];
        if (col < line.Text.Length)
        {
            line.Text = line.Text.Remove(col, 1);
            IsModified = true;
        }
        else if (row < _lines.Count - 1)
        {
            JoinWithNext(row);
        }
    }

    // Merge row+1 into row: the merged line keeps the *lower* line's terminator,
    // because that terminator now ends the combined line.
    private void JoinWithNext(int row)
    {
        Line upper = _lines[row];
        Line lower = _lines[row + 1];
        upper.Text += lower.Text;
        upper.Ending = lower.Ending;
        _lines.RemoveAt(row + 1);
        IsModified = true;
    }

    /// <summary>Clear the modified flag after a successful save.</summary>
    public void MarkSaved() => IsModified = false;

    // The file's majority ending decides what new breaks use. Ties and the
    // no-terminator case fall to LF — the sane default for a new *nix-ish config.
    private static LineEnding DominantEnding(List<Line> lines)
    {
        int lf = 0, crlf = 0, cr = 0;
        foreach (Line line in lines)
        {
            switch (line.Ending)
            {
                case LineEnding.Lf: lf++; break;
                case LineEnding.CrLf: crlf++; break;
                case LineEnding.Cr: cr++; break;
            }
        }
        if (crlf >= lf && crlf >= cr && crlf > 0) return LineEnding.CrLf;
        if (cr > lf && cr > 0) return LineEnding.Cr;
        return LineEnding.Lf;
    }
}
