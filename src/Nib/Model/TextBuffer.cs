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

    // ---- ranged / multi-line edits (phase 4) --------------------------------
    // The three verbs undo and paste are built from. GetRange reconstructs the
    // text *with each line's real terminator*, and InsertMultiline restores those
    // terminators from the text it is given — so a Delete then Insert of the same
    // range is byte-for-byte identity. Paste stays consistent by normalizing the
    // clipboard text to DefaultEnding before it ever reaches here.

    /// <summary>
    /// The text of [start, end), start ≤ end. Within a line this is a plain
    /// substring; across lines the crossed terminators are included verbatim so the
    /// string can be re-inserted to reproduce the original bytes.
    /// </summary>
    public string GetRange(TextPosition start, TextPosition end)
    {
        if (start.Row == end.Row)
            return _lines[start.Row].Text.Substring(start.Col, end.Col - start.Col);

        var sb = new System.Text.StringBuilder();
        sb.Append(_lines[start.Row].Text[start.Col..]);
        sb.Append(_lines[start.Row].Ending.ToChars());
        for (int r = start.Row + 1; r < end.Row; r++)
        {
            sb.Append(_lines[r].Text);
            sb.Append(_lines[r].Ending.ToChars());
        }
        sb.Append(_lines[end.Row].Text[..end.Col]);
        return sb.ToString();
    }

    /// <summary>
    /// Remove [start, end), start ≤ end. Across lines the boundary lines merge; like
    /// <see cref="JoinWithNext"/> the surviving line keeps the *lower* line's
    /// terminator, since that terminator now ends the combined line.
    /// </summary>
    public void DeleteRange(TextPosition start, TextPosition end)
    {
        if (start.Row == end.Row)
        {
            if (end.Col > start.Col)
            {
                Line only = _lines[start.Row];
                only.Text = only.Text.Remove(start.Col, end.Col - start.Col);
                IsModified = true;
            }
            return;
        }

        Line first = _lines[start.Row];
        Line last = _lines[end.Row];
        first.Text = first.Text[..start.Col] + last.Text[end.Col..];
        first.Ending = last.Ending; // the lower line's terminator ends the merged line
        _lines.RemoveRange(start.Row + 1, end.Row - start.Row);
        IsModified = true;
    }

    /// <summary>
    /// Insert <paramref name="text"/> at <paramref name="at"/> and return the
    /// position just past it. Line breaks embedded in the text (LF/CRLF/CR) become
    /// real line terminators, preserved exactly — this is what lets undo restore
    /// mixed endings. The tail of the split line keeps the original line's ending.
    /// </summary>
    public TextPosition InsertMultiline(TextPosition at, string text)
    {
        if (text.Length == 0) return at;

        (List<string> segments, List<LineEnding> breaks) = SplitOnEndings(text);
        Line line = _lines[at.Row];

        if (breaks.Count == 0)
        {
            // No embedded newline: a plain in-line insert.
            line.Text = line.Text.Insert(at.Col, segments[0]);
            IsModified = true;
            return new TextPosition(at.Row, at.Col + segments[0].Length);
        }

        string head = line.Text[..at.Col];
        string tail = line.Text[at.Col..];
        LineEnding tailEnding = line.Ending;

        // First segment finishes the head line; it takes the first embedded break.
        line.Text = head + segments[0];
        line.Ending = breaks[0];

        // Whole middle segments become their own lines with their own breaks.
        int row = at.Row;
        for (int i = 1; i < segments.Count - 1; i++)
        {
            row++;
            _lines.Insert(row, new Line(segments[i], breaks[i]));
        }

        // Last segment carries the original tail and the original line's ending.
        string lastSeg = segments[^1];
        row++;
        _lines.Insert(row, new Line(lastSeg + tail, tailEnding));
        IsModified = true;
        return new TextPosition(row, lastSeg.Length);
    }

    /// <summary>
    /// The position just past <paramref name="text"/> if it were laid out starting at
    /// <paramref name="start"/>. Undo uses this to find the span an edit occupies
    /// without re-scanning the buffer.
    /// </summary>
    public static TextPosition Advance(TextPosition start, string text)
    {
        int breaks = 0, lastBreak = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') { breaks++; lastBreak = i; }
            else if (c == '\r')
            {
                breaks++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                lastBreak = i;
            }
        }
        return breaks == 0
            ? new TextPosition(start.Row, start.Col + text.Length)
            : new TextPosition(start.Row + breaks, text.Length - lastBreak - 1);
    }

    // Split text into segments and the break that followed each (all but the last).
    // \r\n is matched before a bare \r so a CRLF is never read as two breaks.
    private static (List<string> Segments, List<LineEnding> Breaks) SplitOnEndings(string text)
    {
        var segments = new List<string>();
        var breaks = new List<LineEnding>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                segments.Add(text[start..i]); breaks.Add(LineEnding.CrLf); i += 2; start = i;
            }
            else if (c == '\r')
            {
                segments.Add(text[start..i]); breaks.Add(LineEnding.Cr); i++; start = i;
            }
            else if (c == '\n')
            {
                segments.Add(text[start..i]); breaks.Add(LineEnding.Lf); i++; start = i;
            }
            else i++;
        }
        segments.Add(text[start..]);
        return (segments, breaks);
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
