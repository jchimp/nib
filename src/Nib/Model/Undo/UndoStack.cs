namespace Nib.Model.Undo;

/// <summary>
/// The undo/redo history. The caller mutates the buffer itself and then
/// <see cref="Record"/>s an <see cref="Edit"/> describing what it did; this class
/// only reverses or replays those edits. Keeping application in the caller and
/// history here is what lets the whole thing be tested with no console attached.
///
/// Consecutive typed characters (and consecutive deletes) collapse into one undo
/// step when they are on the same line, contiguous, and within a short time window,
/// so undoing a typed word removes the word rather than one letter at a time.
/// </summary>
public sealed class UndoStack
{
    // 300 ms, the pause that ends a "run" of typing. Injected clock keeps the
    // coalescence deterministic under test.
    private static readonly long CoalesceWindowTicks = TimeSpan.FromMilliseconds(300).Ticks;

    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private readonly Func<long> _now;

    public UndoStack(Func<long>? nowTicks = null)
        => _now = nowTicks ?? (() => DateTime.UtcNow.Ticks);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The current wall clock in ticks, for the caller to stamp its edits.</summary>
    public long Now => _now();

    /// <summary>
    /// Add an edit to the history. Any pending redo is discarded (the timeline just
    /// forked). A typing/delete run merges into the top edit instead of stacking.
    /// </summary>
    public void Record(Edit edit)
    {
        _redo.Clear();

        if (_undo.Count > 0 && TryCoalesce(_undo.Peek(), edit, out Edit merged))
        {
            _undo.Pop();
            _undo.Push(merged);
            return;
        }

        _undo.Push(edit);
    }

    /// <summary>Reverse the most recent edit and restore the pre-edit caret. False if nothing to undo.</summary>
    public bool Undo(TextBuffer buffer, Cursor cursor)
    {
        if (_undo.Count == 0) return false;

        Edit e = _undo.Pop();
        buffer.DeleteRange(e.Start, TextBuffer.Advance(e.Start, e.Inserted));
        buffer.InsertMultiline(e.Start, e.Removed);
        cursor.MoveTo(e.CaretBefore.Row, e.CaretBefore.Col);
        _redo.Push(e);
        return true;
    }

    /// <summary>Replay the most recently undone edit and restore the post-edit caret. False if nothing to redo.</summary>
    public bool Redo(TextBuffer buffer, Cursor cursor)
    {
        if (_redo.Count == 0) return false;

        Edit e = _redo.Pop();
        buffer.DeleteRange(e.Start, TextBuffer.Advance(e.Start, e.Removed));
        buffer.InsertMultiline(e.Start, e.Inserted);
        cursor.MoveTo(e.CaretAfter.Row, e.CaretAfter.Col);
        _undo.Push(e);
        return true;
    }

    // Merge `next` into `top` when they are the same kind of single-line run,
    // contiguous, and close in time. Returns the combined edit; otherwise false.
    private static bool TryCoalesce(Edit top, Edit next, out Edit merged)
    {
        merged = top;
        if (!top.Coalesce || !next.Coalesce) return false;
        if (next.TimestampTicks - top.TimestampTicks > CoalesceWindowTicks) return false;

        // Extend a run of typing: next inserts exactly where top's insert ended.
        if (top.IsInsert && next.IsInsert && SingleLine(top.Inserted) && SingleLine(next.Inserted)
            && next.Start == TextBuffer.Advance(top.Start, top.Inserted))
        {
            merged = top with
            {
                Inserted = top.Inserted + next.Inserted,
                CaretAfter = next.CaretAfter,
                TimestampTicks = next.TimestampTicks,
            };
            return true;
        }

        if (top.IsDelete && next.IsDelete && SingleLine(top.Removed) && SingleLine(next.Removed))
        {
            // Forward-delete run: caret fixed, each Delete removes the next char at Start.
            if (next.Start == top.Start)
            {
                merged = top with
                {
                    Removed = top.Removed + next.Removed,
                    CaretAfter = next.CaretAfter,
                    TimestampTicks = next.TimestampTicks,
                };
                return true;
            }

            // Backspace run: next removes the char immediately before top's span.
            if (next.Start.Row == top.Start.Row && next.Start.Col + next.Removed.Length == top.Start.Col)
            {
                merged = top with
                {
                    Start = next.Start,
                    Removed = next.Removed + top.Removed,
                    CaretAfter = next.CaretAfter,
                    TimestampTicks = next.TimestampTicks,
                };
                return true;
            }
        }

        return false;
    }

    private static bool SingleLine(string s) => s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0;
}
