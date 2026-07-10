namespace BgTtsVoicePatcher.Tlk;

/// <summary>
/// In-memory representation of a single TLK V1 strref entry (26 bytes on disk).
/// Layout reference: https://gibberlings3.github.io/iesdp/file_formats/ie_formats/tlk_v1.htm
/// </summary>
public sealed class TlkEntry
{
    public const int FlagTextExists = 0x0001;
    public const int FlagSoundExists = 0x0002;
    public const int FlagTokenExists = 0x0004;

    /// <summary>0-based index into the TLK file - this *is* the StrRef used everywhere
    /// else in the engine (DLG files, CRE files, etc).</summary>
    public required int StrRef { get; init; }

    /// <summary>Absolute byte offset of this entry's 26-byte record in the file.
    /// Used to patch Flags/SoundResRef in place without touching anything else.</summary>
    public required long EntryFileOffset { get; init; }

    public ushort Flags { get; set; }
    public string SoundResRef { get; set; } = string.Empty;
    public uint VolumeVariance { get; set; }
    public uint PitchVariance { get; set; }
    public uint StringOffsetRelative { get; set; }
    public uint StringLength { get; set; }

    /// <summary>Decoded text, populated once the strings section is read.</summary>
    public string Text { get; set; } = string.Empty;

    public bool HasText => (Flags & FlagTextExists) != 0;

    public bool HasSound => (Flags & FlagSoundExists) != 0 && !IsBlankResRef(SoundResRef);

    private static bool IsBlankResRef(string resRef) =>
        string.IsNullOrWhiteSpace(resRef) || resRef.Trim('\0', ' ').Length == 0;
}
