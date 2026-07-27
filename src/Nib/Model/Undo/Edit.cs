namespace Nib.Model.Undo;

/// <summary>
/// One undoable change, expressed as the single shape every mutation reduces to:
/// at <see cref="Start"/>, the text <see cref="Removed"/> was replaced by
/// <see cref="Inserted"/>. Undo swaps the two; redo re-applies. Insert, delete,
/// backspace, paste, cut, and select-then-type are all this record with different
/// fields filled in.
///
/// <see cref="CaretBefore"/>/<see cref="CaretAfter"/> restore the caret where the
/// user expects it on undo/redo. <see cref="TimestampTicks"/> and
/// <see cref="Coalesce"/> drive typing/delete run merging in <see cref="UndoStack"/>;
/// operations that must start a fresh undo group (paste, cut, Enter, select-delete)
/// set <see cref="Coalesce"/> to false.
/// </summary>
public sealed record Edit(
    TextPosition Start,
    string Removed,
    string Inserted,
    TextPosition CaretBefore,
    TextPosition CaretAfter,
    long TimestampTicks,
    bool Coalesce)
{
    /// <summary>A pure insertion (typing, paste into an empty selection).</summary>
    public bool IsInsert => Removed.Length == 0 && Inserted.Length > 0;

    /// <summary>A pure deletion (backspace, Delete, cut).</summary>
    public bool IsDelete => Inserted.Length == 0 && Removed.Length > 0;
}
