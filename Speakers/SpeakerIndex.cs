using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BgTtsVoicePatcher.Dlg;
using BgTtsVoicePatcher.State;

namespace BgTtsVoicePatcher.Speakers;

/// <summary>
/// Builds a StrRef -> speaker-name lookup by scanning every loose .dlg file in a
/// directory (typically the game's override folder, or a Near-Infinity mass-export
/// folder covering BIFF-packed content too). The "speaker name" is just the DLG
/// file's own resref - by Bioware convention this is usually the NPC's name, but for
/// generic/shared dialogues (mobs, shopkeepers, chapter-split copies of the same NPC)
/// it may not read as a clean character name. There's no attempt to normalise or group
/// these - that's what the display-name grouping in speaker-names.json is for, sourced
/// from each speaker's CRE rather than guessed from the DLG name itself.
/// </summary>
public static class SpeakerIndex
{
    public sealed record ScanResult(
        Dictionary<int, string> StrRefToSpeaker,
        Dictionary<string, int> LineCountsByName,
        int FilesScanned,
        int FilesFailed);

    /// <summary>One system (DLG-derived) name's resolved info, ready to be written
    /// into speaker-names.json grouped under DisplayName.</summary>
    public sealed record SpeakerInfo(string SystemName, string? DisplayName, Gender Gender);

    public static ScanResult Scan(string dlgDirectory)
    {
        var strRefToSpeaker = new Dictionary<int, string>();
        var lineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var filesScanned = 0;
        var filesFailed = 0;

        foreach (var path in Directory.EnumerateFiles(dlgDirectory, "*.dlg"))
        {
            var speakerName = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();

            try
            {
                var strRefs = DlgFile.ReadStateStrRefs(path);
                filesScanned++;

                foreach (var strRef in strRefs)
                {
                    // First DLG found "owning" a StrRef wins; in practice a given
                    // StrRef only ever appears as actor-text in one place.
                    strRefToSpeaker.TryAdd(strRef, speakerName);
                }

                if (strRefs.Count > 0)
                    lineCounts[speakerName] = lineCounts.GetValueOrDefault(speakerName) + strRefs.Count;
            }
            catch (Exception)
            {
                filesFailed++;
            }
        }

        return new ScanResult(strRefToSpeaker, lineCounts, filesScanned, filesFailed);
    }

    public static void SaveStrRefMap(Dictionary<int, string> map, string path)
    {
        var json = JsonSerializer.Serialize(
            map.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static Dictionary<int, string> LoadStrRefMap(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<int, string>();

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                  ?? new Dictionary<string, string>();

        return raw.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
    }

    /// <summary>
    /// Writes (or merges into) the hand-edited names file, grouped by display name:
    ///   { "Jaheira": { "JAHEIRA": "F", "BJAHEIR": "F", "TTJAHEIR": "F" }, ... }
    /// Once a system name appears anywhere in the file, it's never touched again on a
    /// later run - whether its value is a CRE-detected guess, a null you haven't
    /// gotten to yet, or a correction you've already made. Only system names that
    /// have never been seen before get added, under whichever display name (or, with
    /// no CRE name available, the system name itself) they resolved to this run.
    /// </summary>
    public static void SaveOrMergeNamesFile(IEnumerable<SpeakerInfo> speakers, string path)
    {
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        var existingSystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in root)
        {
            if (group.Value is JsonObject members)
                foreach (var member in members)
                    existingSystemNames.Add(member.Key);
        }

        foreach (var speaker in speakers.OrderByDescending(s => s.DisplayName is not null))
        {
            if (existingSystemNames.Contains(speaker.SystemName))
                continue;

            var groupKey = string.IsNullOrWhiteSpace(speaker.DisplayName) ? speaker.SystemName : speaker.DisplayName;

            if (root[groupKey] is not JsonObject group)
            {
                group = new JsonObject();
                root[groupKey] = group;
            }

            group[speaker.SystemName] = speaker.Gender switch
            {
                Gender.Male => "M",
                Gender.Female => "F",
                _ => null
            };

            existingSystemNames.Add(speaker.SystemName);
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
