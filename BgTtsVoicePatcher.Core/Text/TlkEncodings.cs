using System.Text;

namespace BgTtsVoicePatcher.Text;

/// <summary>
/// Resolves the Encoding to use for reading/writing dialog.tlk text, and guarantees
/// CodePagesEncodingProvider is registered before any codepage lookup happens -
/// regardless of which entry point (console Program.cs or the WPF app) calls in
/// first. Centralized here rather than duplicated per-entry-point so this can't be
/// forgotten in one of them.
///
/// dialog.tlk stores text as raw bytes in whatever single-byte Windows codepage
/// matches the game's language - there's no marker in the file format itself.
/// en_US and most Western European localizations use windows-1252; Czech/Polish/
/// Hungarian fan translations typically use windows-1250 (Central European). Pure
/// ASCII (0x00-0x7F) is identical across both, which is why English-only content
/// never reveals a missing codepage registration - the gap only shows up at smart
/// quotes, em-dashes, and accented characters (0x80+).
/// </summary>
public static class TlkEncodings
{
    private static bool _providerRegistered;
    private static readonly object RegisterLock = new();

    /// <summary>Registers CodePagesEncodingProvider if it hasn't been already.
    /// Safe to call repeatedly or from multiple entry points - RegisterProvider
    /// itself is a no-op on repeat registration of the same provider, but this
    /// avoids even that redundant call after the first time.</summary>
    public static void EnsureCodePagesRegistered()
    {
        if (_providerRegistered)
            return;

        lock (RegisterLock)
        {
            if (_providerRegistered)
                return;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _providerRegistered = true;
        }
    }

    /// <summary>Resolves a friendly encoding name ("windows-1252", "windows-1250",
    /// "utf8", "ascii", or a raw codepage number as a string) to an Encoding,
    /// registering codepage support first if needed.</summary>
    public static Encoding Resolve(string name)
    {
        EnsureCodePagesRegistered();

        return name.Trim().ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => Encoding.UTF8,
            "ascii" => Encoding.ASCII,
            "windows-1252" or "1252" => Encoding.GetEncoding(1252),
            "windows-1250" or "1250" => Encoding.GetEncoding(1250),
            _ => Encoding.GetEncoding(name)
        };
    }

    /// <summary>
    /// Returns a list of all available encodings on the current platform, registering codepage support first if needed.
    /// </summary>
    /// <returns>List of encodings</returns>
    public static EncodingInfo[]? GetAvailableEncodings()
    {
        EnsureCodePagesRegistered();
        return Encoding.GetEncodings();
    }
}
