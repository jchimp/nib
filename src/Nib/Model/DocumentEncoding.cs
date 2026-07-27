using System.Text;

namespace Nib.Model;

/// <summary>
/// How the file was decoded and whether it carried a byte-order mark, captured at
/// load so <see cref="FileIo.Save"/> can re-emit the exact same byte shape. The
/// BOM bytes are stored verbatim rather than recomputed, so a file either keeps
/// its BOM or stays without one — we never add or drop one silently.
/// </summary>
/// <param name="Encoding">The encoding used to decode the body (BOM excluded).</param>
/// <param name="Bom">The leading BOM bytes, or empty if the file had none.</param>
public sealed record DocumentEncoding(Encoding Encoding, byte[] Bom)
{
    public bool HasBom => Bom.Length > 0;

    /// <summary>A short label for the status bar (e.g. "UTF-8", "UTF-8 BOM", "UTF-16 LE").</summary>
    public string DisplayName { get; init; } = "UTF-8";

    /// <summary>The default for a new, never-saved buffer: UTF-8, no BOM.</summary>
    public static DocumentEncoding Utf8NoBom { get; } =
        new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), []) { DisplayName = "UTF-8" };
}
