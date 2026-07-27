using System.Runtime.InteropServices;

namespace Nib.Terminal;

/// <summary>
/// System clipboard via user32, plus a private fallback buffer.
///
/// The fallback matters: the Windows clipboard is a shared, lockable resource,
/// and any other process can hold it open. If a copy fails, Ctrl+K / Ctrl+U
/// should still behave like nano's cut buffer rather than silently losing the
/// user's text.
/// </summary>
public static class Clipboard
{
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 12;

    /// <summary>Last text Nib copied, used when the system clipboard is unavailable.</summary>
    public static string FallbackBuffer { get; private set; } = string.Empty;

    /// <summary>True if the most recent operation used the system clipboard rather than the fallback.</summary>
    public static bool LastOperationUsedSystemClipboard { get; private set; }

    // OpenClipboard fails while any other process holds the clipboard open —
    // usually for a few milliseconds. Retry briefly before giving up to the
    // fallback buffer rather than failing on the first contended attempt.
    private static bool TryOpen()
    {
        for (int i = 0; i < OpenAttempts; i++)
        {
            if (NativeMethods.OpenClipboard(0)) return true;
            Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }

    public static string GetText()
    {
        LastOperationUsedSystemClipboard = false;

        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
            return FallbackBuffer;

        if (!TryOpen()) return FallbackBuffer;

        try
        {
            nint handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (handle == 0) return FallbackBuffer;

            nint ptr = NativeMethods.GlobalLock(handle);
            if (ptr == 0) return FallbackBuffer;

            try
            {
                string? text = Marshal.PtrToStringUni(ptr);
                if (text is null) return FallbackBuffer;
                LastOperationUsedSystemClipboard = true;
                return text;
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static bool SetText(string text)
    {
        FallbackBuffer = text;
        LastOperationUsedSystemClipboard = false;

        if (!TryOpen()) return false;

        nint global = 0;
        try
        {
            if (!NativeMethods.EmptyClipboard()) return false;

            int charCount = text.Length + 1; // include the terminating NUL
            global = NativeMethods.GlobalAlloc(
                NativeMethods.GMEM_MOVEABLE, (nuint)(charCount * sizeof(char)));
            if (global == 0) return false;

            nint ptr = NativeMethods.GlobalLock(global);
            if (ptr == 0) return false;

            try
            {
                char[] buffer = new char[charCount];
                text.CopyTo(0, buffer, 0, text.Length);
                buffer[text.Length] = '\0';
                Marshal.Copy(buffer, 0, ptr, charCount);
            }
            finally
            {
                NativeMethods.GlobalUnlock(global);
            }

            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, global) == 0)
                return false;

            // Ownership of the block transferred to the system on success.
            // Freeing it here would be a use-after-free for every other app.
            global = 0;
            LastOperationUsedSystemClipboard = true;
            return true;
        }
        finally
        {
            if (global != 0) NativeMethods.GlobalFree(global);
            NativeMethods.CloseClipboard();
        }
    }
}
