using System.Text;
using Nib.Model;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The most important tests in the project: load a file and save it with no edits;
/// the bytes must be identical. Covers encoding, BOM, and per-line endings. These
/// run with no console attached — the whole point of keeping <c>Model/</c> free of
/// <c>Terminal/</c>.
/// </summary>
public class FileIoRoundTripTests
{
    // Write bytes to a temp file, Load then Save (no edits), return the bytes written.
    private static byte[] RoundTrip(byte[] input)
    {
        string src = Path.Combine(Path.GetTempPath(), $"nib-rt-{Guid.NewGuid():N}.txt");
        string dst = Path.Combine(Path.GetTempPath(), $"nib-rt-{Guid.NewGuid():N}.out");
        try
        {
            File.WriteAllBytes(src, input);
            TextBuffer buffer = FileIo.Load(src);
            FileIo.Save(buffer, dst);
            return File.ReadAllBytes(dst);
        }
        finally
        {
            File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    private static void AssertRoundTrips(byte[] input) =>
        Assert.Equal(input, RoundTrip(input));

    [Fact]
    public void Lf_file_round_trips()
        => AssertRoundTrips("server:\n  port = 8080\n"u8.ToArray());

    [Fact]
    public void Crlf_file_round_trips()
        => AssertRoundTrips("server:\r\n  port = 8080\r\n"u8.ToArray());

    [Fact]
    public void No_trailing_newline_round_trips()
        => AssertRoundTrips("one\ntwo"u8.ToArray());

    [Fact]
    public void Mixed_endings_are_preserved_per_line()
        => AssertRoundTrips("crlf\r\nlf\ncr\rlast"u8.ToArray());

    [Fact]
    public void Empty_file_round_trips()
        => AssertRoundTrips([]);

    [Fact]
    public void Utf8_bom_round_trips()
    {
        byte[] input = [0xEF, 0xBB, 0xBF, .. "café\n"u8.ToArray()];
        AssertRoundTrips(input);
    }

    [Fact]
    public void Utf16_le_bom_round_trips()
    {
        byte[] body = new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("café\nsecond\n");
        byte[] input = [0xFF, 0xFE, .. body];
        AssertRoundTrips(input);
    }

    [Fact]
    public void Utf16_be_bom_round_trips()
    {
        byte[] body = new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes("data\n");
        byte[] input = [0xFE, 0xFF, .. body];
        AssertRoundTrips(input);
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_latin1_and_round_trips()
    {
        // 0xE9 is 'é' in Latin-1 but a lone continuation-less lead byte in UTF-8,
        // so detection must fall back to Latin-1 and preserve the byte.
        byte[] input = [.. "caf"u8.ToArray(), 0xE9, (byte)'\n'];
        AssertRoundTrips(input);
    }

    [Fact]
    public void A_failed_save_leaves_the_original_file_intact()
    {
        // The ROADMAP's "interrupting a save leaves the original intact" check.
        // We can't truly kill the process mid-write, but locking the destination
        // exclusively makes File.Replace fail the same way a crashed/blocked write
        // would — the atomic temp-then-replace must not touch the original.
        byte[] original = "port = 8080\nkeep this exactly\n"u8.ToArray();
        string path = Path.Combine(Path.GetTempPath(), $"nib-fail-{Guid.NewGuid():N}.conf");
        File.WriteAllBytes(path, original);
        try
        {
            TextBuffer buffer = FileIo.Load(path);
            buffer.InsertText(0, 0, "CORRUPTED "); // an edit that must NOT reach disk
            Assert.True(buffer.IsModified);

            // Hold the target open with no sharing so the replace cannot succeed.
            using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => FileIo.Save(buffer, path));
            }

            // Original bytes survive untouched, the buffer stays dirty (save never
            // completed), and no temp file is left behind.
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(buffer.IsModified);
            string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.nib-tmp");
            Assert.False(File.Exists(temp), "temp file should be cleaned up on a failed save");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Detected_encoding_labels_are_reported()
    {
        string src = Path.Combine(Path.GetTempPath(), $"nib-enc-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllBytes(src, "plain\n"u8.ToArray());
            Assert.Equal("UTF-8", FileIo.Load(src).Encoding.DisplayName);
        }
        finally { File.Delete(src); }
    }
}
