namespace Nib.Highlight;

/// <summary>
/// Picks the TextMate scope for a file: manual override, then extension, then
/// filename, then shebang. Returns null when nothing matches, which the editor
/// renders uncolored with no error — most of what nano opens has no grammar and
/// that is fine.
///
/// Extension before filename is deliberate and the opposite of what VS Code does.
/// Our filename table exists to catch extensionless files and to disambiguate
/// (<c>docker-compose.yml</c>), never to override a perfectly good extension.
/// </summary>
public sealed class LanguageDetector
{
    private readonly GrammarManifest _manifest;

    public LanguageDetector(GrammarManifest manifest) => _manifest = manifest;

    /// <summary>
    /// The scope for <paramref name="path"/>, or null.
    /// <paramref name="firstLine"/> is the buffer's first line, consulted only for a
    /// shebang when path-based detection found nothing.
    /// <paramref name="overrideScope"/> short-circuits everything (the future
    /// "set syntax" command; wired now so the policy lives in one place).
    /// </summary>
    public string? Detect(string? path, string? firstLine, string? overrideScope = null)
    {
        if (!string.IsNullOrEmpty(overrideScope)) return overrideScope;

        if (!string.IsNullOrEmpty(path))
        {
            string name = System.IO.Path.GetFileName(path);

            string ext = System.IO.Path.GetExtension(name);
            if (ext.Length > 0 && _manifest.ScopeForExtension(ext) is { } byExt) return byExt;

            if (_manifest.ScopeForFilename(name) is { } byName) return byName;

            // Dotfiles: GetExtension(".bashrc") returns "" because the whole name is
            // treated as the stem, so the extension table never sees it. Retry the
            // name as though it were an extension — that is how the manifest lists
            // ".bashrc" and ".gitconfig".
            if (name.StartsWith('.') && _manifest.ScopeForExtension(name) is { } byDotfile) return byDotfile;
        }

        return ShebangScope(firstLine);
    }

    // "#!/usr/bin/env python3" and "#!/bin/bash -e" both have to land on the
    // interpreter: take the last path segment of the first token after the marker,
    // and skip `env` so the argument after it is used instead.
    private string? ShebangScope(string? firstLine)
    {
        if (firstLine is null || !firstLine.StartsWith("#!", StringComparison.Ordinal)) return null;

        string[] words = firstLine[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words)
        {
            if (word.StartsWith('-')) continue; // a flag, not the interpreter

            string leaf = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
            if (leaf.Length == 0) continue;
            if (string.Equals(leaf, "env", StringComparison.OrdinalIgnoreCase)) continue;

            return _manifest.ScopeForShebang(leaf);
        }

        return null;
    }
}
