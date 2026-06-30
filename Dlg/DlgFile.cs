using System.Text;

namespace BgTtsVoicePatcher.Dlg;

/// <summary>
/// Minimal DLG V1.0 reader: only extracts each state's actor-response StrRef (i.e.
/// "what this NPC says"), since that's all that's needed to attribute dialog.tlk
/// lines to a speaker. Deliberately ignores the transition table (player response
/// text) and triggers/actions - the player's own lines aren't a "speaker" for our
/// purposes, and triggers/actions don't bear on gender at all.
///
/// All section offsets are read directly from the header rather than assumed, so this
/// works whether or not the file has BG2/EE's extra trailing flags field that BG1's
/// original DLG files lack.
///
/// Layout reference: https://iesdp.bgforge.net/file_formats/ie_formats/dlg_v1.htm
/// </summary>
public static class DlgFile
{
    /// <summary>Returns the StrRef of every actor-response (NPC line) state in the
    /// file. Returns an empty list - rather than throwing - for anything that isn't a
    /// recognisable DLG V1.0 file, so a scan over hundreds of files can't be derailed
    /// by one oddity. Callers should still wrap calls in try/catch for I/O errors.</summary>
    public static List<int> ReadStateStrRefs(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (stream.Length < 0x30)
            return new List<int>();

        var signature = new string(reader.ReadChars(4));
        var version = new string(reader.ReadChars(4));

        if (signature != "DLG " || !version.StartsWith("V1", StringComparison.Ordinal))
            return new List<int>();

        var numStates = reader.ReadInt32();
        var stateTableOffset = reader.ReadInt32();

        if (numStates <= 0 || stateTableOffset < 0 || stateTableOffset >= stream.Length)
            return new List<int>();

        var strRefs = new List<int>(numStates);
        stream.Seek(stateTableOffset, SeekOrigin.Begin);

        for (var i = 0; i < numStates; i++)
        {
            if (stream.Position + 16 > stream.Length)
                break; // truncated/corrupt file - keep whatever we already found

            var strRef = reader.ReadInt32();
            reader.ReadInt32(); // first transition index - unused
            reader.ReadInt32(); // transition count - unused
            reader.ReadInt32(); // trigger index - unused

            if (strRef >= 0)
                strRefs.Add(strRef);
        }

        return strRefs;
    }
}
