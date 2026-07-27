namespace Nib.Model;

/// <summary>
/// The terminator that followed a line in the source file. Stored per line so a
/// file with mixed endings round-trips exactly (a real hazard in configs edited
/// on more than one OS). <see cref="None"/> marks a final line the file did not
/// terminate — it emits no bytes on save.
/// </summary>
public enum LineEnding
{
    Lf,
    CrLf,
    Cr,
    None,
}

public static class LineEndingExtensions
{
    /// <summary>The characters this ending contributes to the reconstructed text.</summary>
    public static string ToChars(this LineEnding ending) => ending switch
    {
        LineEnding.Lf => "\n",
        LineEnding.CrLf => "\r\n",
        LineEnding.Cr => "\r",
        LineEnding.None => "",
        _ => "\n",
    };
}
