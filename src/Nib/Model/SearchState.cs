namespace Nib.Model;

/// <summary>
/// What the last search asked for, so ^F pre-fills the prompt, F3 repeats with no
/// prompt at all, and the case/whole-word toggles survive between searches the way
/// nano's do.
///
/// It lives on <see cref="Nib.Commands.EditorCommands"/> rather than on the editor
/// loop so that "find again with nothing to find again" is a testable outcome
/// instead of a null check in Program.cs.
/// </summary>
public sealed class SearchState
{
    public string Text { get; set; } = "";
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }

    /// <summary>The last replacement, so ^R pre-fills that half of the gesture too.</summary>
    public string Replacement { get; set; } = "";

    public bool HasQuery => Text.Length > 0;

    public SearchQuery Query => new(Text, MatchCase, WholeWord);
}
