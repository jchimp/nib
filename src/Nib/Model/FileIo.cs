using System.Text;

namespace Nib.Model;

/// <summary>
/// Load and save that preserve the file's bytes: its encoding, its BOM (or
/// absence of one), and each line's own terminator. Console-free by rule.
///
/// The single most important behaviour in the project is that loading a file and
/// saving it with no edits produces identical bytes — that is what keeps Nib from
/// quietly corrupting a config. Detection order and the atomic write below both
/// exist to protect it.
/// </summary>
public static class FileIo
{
    // Strict UTF-8: throw on invalid bytes so we can tell a real UTF-8 file from a
    // legacy single-byte one, instead of silently substituting U+FFFD (which would
    // make a save lossy).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static TextBuffer Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        (DocumentEncoding enc, int bomLen) = DetectEncoding(bytes);
        string text = enc.Encoding.GetString(bytes, bomLen, bytes.Length - bomLen);
        return new TextBuffer(SplitLines(text), enc, path);
    }

    /// <summary>
    /// Write <paramref name="buffer"/> to <paramref name="path"/> atomically: a temp
    /// file in the same directory, then a replace. An interrupted or failed write
    /// leaves the original file untouched — never a truncated config.
    /// </summary>
    public static void Save(TextBuffer buffer, string path)
    {
        DocumentEncoding enc = buffer.Encoding;
        byte[] body = enc.Encoding.GetBytes(buffer.ToText());

        byte[] output;
        if (enc.HasBom)
        {
            output = new byte[enc.Bom.Length + body.Length];
            Array.Copy(enc.Bom, output, enc.Bom.Length);
            Array.Copy(body, 0, output, enc.Bom.Length, body.Length);
        }
        else
        {
            output = body;
        }

        // Same directory so File.Replace stays on one volume (a cross-volume
        // replace is a copy, which defeats atomicity).
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.nib-tmp");

        File.WriteAllBytes(temp, output);
        try
        {
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null);
            else
                File.Move(temp, path);
        }
        catch
        {
            // Best-effort cleanup; leave the original intact regardless.
            try { File.Delete(temp); } catch { /* ignore */ }
            throw;
        }

        buffer.Path = path;
        buffer.MarkSaved();
    }

    // BOM sniffing. UTF-32 LE (FF FE 00 00) must be checked before UTF-16 LE
    // (FF FE) because the shorter mark is a prefix of the longer one.
    private static (DocumentEncoding, int BomLength) DetectEncoding(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00)
            return (new DocumentEncoding(new UTF32Encoding(bigEndian: false, byteOrderMark: false), b[..4]) { DisplayName = "UTF-32 LE" }, 4);
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF)
            return (new DocumentEncoding(new UTF32Encoding(bigEndian: true, byteOrderMark: false), b[..4]) { DisplayName = "UTF-32 BE" }, 4);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return (new DocumentEncoding(new UTF8Encoding(false), b[..3]) { DisplayName = "UTF-8 BOM" }, 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return (new DocumentEncoding(new UnicodeEncoding(bigEndian: false, byteOrderMark: false), b[..2]) { DisplayName = "UTF-16 LE" }, 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return (new DocumentEncoding(new UnicodeEncoding(bigEndian: true, byteOrderMark: false), b[..2]) { DisplayName = "UTF-16 BE" }, 2);

        // No BOM: assume UTF-8, but fall back to Latin1 if it doesn't decode. Latin1
        // maps every byte 0x00-0xFF reversibly, so a legacy config still round-trips
        // exactly — and it needs no code-page package (see the plan's encoding note).
        try
        {
            _ = StrictUtf8.GetString(b);
            return (new DocumentEncoding(new UTF8Encoding(false), []) { DisplayName = "UTF-8" }, 0);
        }
        catch (DecoderFallbackException)
        {
            return (new DocumentEncoding(Encoding.Latin1, []) { DisplayName = "Latin-1" }, 0);
        }
    }

    // Split into lines, recording each terminator. A trailing terminator yields a
    // final empty line with LineEnding.None (which emits nothing on save), so the
    // byte count is preserved either way.
    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                lines.Add(new Line(text[start..i], LineEnding.Lf));
                start = i + 1;
            }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    lines.Add(new Line(text[start..i], LineEnding.CrLf));
                    i++; // consume the \n of \r\n
                }
                else
                {
                    lines.Add(new Line(text[start..i], LineEnding.Cr));
                }
                start = i + 1;
            }
        }
        lines.Add(new Line(text[start..], LineEnding.None));
        return lines;
    }
}
