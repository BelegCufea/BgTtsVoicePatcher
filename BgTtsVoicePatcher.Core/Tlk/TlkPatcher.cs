using System.IO;
using System.Text;

namespace BgTtsVoicePatcher.Tlk;

/// <summary>
/// Opens a TLK file for in-place binary patching. This only ever rewrites the
/// fixed-width Flags (2 bytes) and SoundResRef (8 bytes) fields of an existing entry -
/// it never touches the strings section or any other entry, so the file's size and
/// every other offset in it stay exactly as they were. That's what makes patching
/// "in place" safe: nothing downstream needs to shift.
/// </summary>
public sealed class TlkPatcher : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;

    public string Path { get; }

    private TlkPatcher(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
        _writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
    }

    public static TlkPatcher Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        return new TlkPatcher(path, stream);
    }

    /// <summary>
    /// Sets the SoundResRef and the "sound exists" flag for an entry, and flushes to
    /// disk immediately - so progress is durable even if the process is killed mid-run.
    /// </summary>
    public void ApplySound(TlkEntry entry, string resRef)
    {
        if (resRef.Length is 0 or > 8)
            throw new ArgumentException($"ResRef '{resRef}' must be 1-8 characters.", nameof(resRef));

        var paddedResRef = resRef.PadRight(8, '\0');
        var resRefBytes = Encoding.ASCII.GetBytes(paddedResRef);
        var newFlags = (ushort)(entry.Flags | TlkEntry.FlagSoundExists);

        _stream.Seek(entry.EntryFileOffset, SeekOrigin.Begin);
        _writer.Write(newFlags);
        _writer.Write(resRefBytes);
        _writer.Flush();
        _stream.Flush(flushToDisk: true);

        entry.Flags = newFlags;
        entry.SoundResRef = resRef;
    }

    /// <summary>
    /// The inverse of ApplySound: clears the "sound exists" flag and zeroes the
    /// SoundResRef back to blank. Used by unpatch to remove this tool's entries
    /// from dialog.tlk without touching anything written by the base game or other mods.
    /// </summary>
    public void ClearSound(TlkEntry entry)
    {
        var blankResRef = new byte[8];
        var newFlags = (ushort)(entry.Flags & ~TlkEntry.FlagSoundExists);

        _stream.Seek(entry.EntryFileOffset, SeekOrigin.Begin);
        _writer.Write(newFlags);
        _writer.Write(blankResRef);
        _writer.Flush();
        _stream.Flush(flushToDisk: true);

        entry.Flags = newFlags;
        entry.SoundResRef = string.Empty;
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}
