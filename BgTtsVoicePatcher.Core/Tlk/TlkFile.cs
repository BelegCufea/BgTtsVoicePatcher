using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BgTtsVoicePatcher.Tlk;

public sealed class TlkHeader
{
    public const int HeaderSize = 18;
    public const int EntrySize = 26;

    public required string Signature { get; init; }
    public required string Version { get; init; }
    public required ushort LanguageId { get; init; }
    public required uint EntryCount { get; init; }
    public required uint StringsOffset { get; init; }
}

/// <summary>
/// Read-only parse of a dialog.tlk (TLK V1) file: header, all 26-byte entries, and
/// the decoded text for each entry that has any. Loading never mutates the file -
/// writing is handled separately by <see cref="TlkPatcher"/>.
/// </summary>
public sealed class TlkFile
{
    public required TlkHeader Header { get; init; }
    public required List<TlkEntry> Entries { get; init; }
    public required string SourcePath { get; init; }

    public static TlkFile Load(string path, Encoding textEncoding)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var signature = new string(reader.ReadChars(4));
        var version = new string(reader.ReadChars(4));

        if (signature != "TLK ")
            throw new InvalidDataException(
                $"Unexpected TLK signature '{signature}' in '{path}'. Is this really a dialog.tlk?");

        if (!version.StartsWith("V1", StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Unsupported TLK version '{version}' in '{path}'. Only V1 is supported.");

        var languageId = reader.ReadUInt16();
        var entryCount = reader.ReadUInt32();
        var stringsOffset = reader.ReadUInt32();

        var header = new TlkHeader
        {
            Signature = signature,
            Version = version,
            LanguageId = languageId,
            EntryCount = entryCount,
            StringsOffset = stringsOffset
        };

        var entries = new List<TlkEntry>((int)entryCount);

        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = TlkHeader.HeaderSize + (long)i * TlkHeader.EntrySize;

            var flags = reader.ReadUInt16();
            var soundResRefBytes = reader.ReadBytes(8);
            var volumeVariance = reader.ReadUInt32();
            var pitchVariance = reader.ReadUInt32();
            var stringOffsetRelative = reader.ReadUInt32();
            var stringLength = reader.ReadUInt32();

            var soundResRef = Encoding.ASCII.GetString(soundResRefBytes).TrimEnd('\0', ' ');

            entries.Add(new TlkEntry
            {
                StrRef = i,
                EntryFileOffset = entryOffset,
                Flags = flags,
                SoundResRef = soundResRef,
                VolumeVariance = volumeVariance,
                PitchVariance = pitchVariance,
                StringOffsetRelative = stringOffsetRelative,
                StringLength = stringLength
            });
        }

        // Strings section: read each entry's text. Offsets are relative to
        // header.StringsOffset, so each lookup is an absolute seek.
        foreach (var entry in entries)
        {
            if (entry.StringLength == 0)
                continue;

            var absoluteOffset = (long)header.StringsOffset + entry.StringOffsetRelative;
            stream.Seek(absoluteOffset, SeekOrigin.Begin);
            var bytes = reader.ReadBytes((int)entry.StringLength);
            entry.Text = textEncoding.GetString(bytes).TrimEnd('\0');
        }

        return new TlkFile { Header = header, Entries = entries, SourcePath = path };
    }
}
