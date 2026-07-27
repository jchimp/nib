namespace Nib.Commands;

/// <summary>
/// The clipboard as <see cref="EditorCommands"/> sees it: plain text in, plain text
/// out. A one-method-each seam so cut/copy/paste can be tested with a fake instead
/// of driving the real Win32 clipboard — and so the editing logic keeps no direct
/// dependency on <c>Terminal/</c>. Line-ending normalization is the caller's job
/// (it needs the buffer's dominant ending), so this stays a pure passthrough.
/// </summary>
public interface IClipboard
{
    string GetText();
    bool SetText(string text);
}

/// <summary>The real clipboard: a thin adapter over <see cref="Terminal.Clipboard"/>.</summary>
public sealed class SystemClipboard : IClipboard
{
    public string GetText() => Terminal.Clipboard.GetText();
    public bool SetText(string text) => Terminal.Clipboard.SetText(text);
}
