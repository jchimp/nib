namespace Nib.Model;

/// <summary>
/// Lines, words and characters — what ^W reports, for the buffer or for a
/// selection. Console-free like the rest of <c>Model/</c>.
///
/// A <b>word</b> is a run of non-whitespace, which is <c>wc -w</c>'s rule and nano's,
/// and the same rule <see cref="Cursor.WordLeft"/>/<see cref="Cursor.WordRight"/>
/// already use for ^arrow movement. It deliberately disagrees with the whole-word
/// search boundary in <see cref="TextSearch"/>, which counts letters, digits and
/// underscore: "foo,bar" is one word to move through and two to match, and both are
/// right for what they do.
///
/// <b>Chars</b> counts line terminators, because they are characters the file holds —
/// so a CRLF line costs two. It is a character count and not a byte count: the two
/// differ the moment a file is UTF-16 or holds anything outside ASCII, and the byte
/// count is not what someone asking "how big is this" from inside an editor means.
/// </summary>
public readonly record struct TextStats(int Lines, int Words, int Chars)
{
    /// <summary>Counts for a whole buffer.</summary>
    public static TextStats Of(TextBuffer buffer)
    {
        int words = 0;
        int chars = 0;

        for (int i = 0; i < buffer.LineCount; i++)
        {
            Line line = buffer.LineAt(i);
            words += CountWords(line.Text);
            chars += line.Text.Length + line.Ending.ToChars().Length;
        }

        return new TextStats(LineCount(buffer), words, chars);
    }

    /// <summary>Counts for a span of text, as <see cref="TextBuffer.GetRange"/> returns it.</summary>
    public static TextStats Of(string text)
    {
        if (text.Length == 0) return new TextStats(0, 0, 0);

        int lines = 0;
        int words = 0;

        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            words += CountWords(text.AsSpan(start, i - start));
            lines++;

            if (i >= text.Length) break;
            // CRLF is one terminator, not two lines.
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            i++;
        }

        return new TextStats(lines, words, text.Length);
    }

    /// <summary>
    /// How many lines the file has, nano-style: the trailing empty, unterminated line
    /// left behind by a file that ended with a newline is not a line anyone counts.
    ///
    /// Shared with the "Wrote N lines" message on save deliberately — ^W reporting
    /// 1,284 lines a keystroke after ^S said it wrote 1,283 is the kind of small
    /// disagreement that makes someone stop trusting both numbers.
    /// </summary>
    public static int LineCount(TextBuffer buffer)
    {
        int count = buffer.LineCount;
        if (count > 1)
        {
            Line last = buffer.LineAt(count - 1);
            if (last.Text.Length == 0 && last.Ending == LineEnding.None) count--;
        }
        return count;
    }

    private static int CountWords(ReadOnlySpan<char> text)
    {
        int words = 0;
        bool inWord = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { inWord = false; continue; }
            if (!inWord) { words++; inWord = true; }
        }

        return words;
    }
}
