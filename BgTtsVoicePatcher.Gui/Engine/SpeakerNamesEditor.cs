using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BgTtsVoicePatcher.Gui.Engine;

/// <summary>
/// Writes gender corrections made in the Speaker Review grid back into
/// speaker-names.json, without disturbing its { "Display Name": { "SYSNAME": "M" } }
/// grouping structure - only the specific system-name entries the user actually
/// edited are touched, wherever in the file they happen to live.
/// </summary>
public static class SpeakerNamesEditor
{
    /// <param name="updates">System name -> new gender ("M", "F", or null to clear).</param>
    public static void ApplyGenderUpdates(string speakerNamesPath, IReadOnlyDictionary<string, string?> updates)
    {
        if (updates.Count == 0)
            return;

        if (!File.Exists(speakerNamesPath))
            throw new FileNotFoundException($"speaker-names.json not found at '{speakerNamesPath}'.");

        var root = JsonNode.Parse(File.ReadAllText(speakerNamesPath)) as JsonObject
                   ?? throw new InvalidDataException($"'{speakerNamesPath}' is not a valid JSON object.");

        var remaining = new HashSet<string>(updates.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var group in root.ToList())
        {
            if (group.Value is not JsonObject members)
                continue;

            foreach (var member in members.ToList())
            {
                if (updates.TryGetValue(member.Key, out var newGender))
                {
                    members[member.Key] = newGender;
                    remaining.Remove(member.Key);
                }
            }
        }

        // Any system name we couldn't find an existing entry for gets added under
        // its own name as a fallback group, rather than silently dropping the edit.
        foreach (var systemName in remaining)
        {
            var group = new JsonObject();
            group[systemName] = updates[systemName];
            root[systemName] = group;
        }

        File.WriteAllText(speakerNamesPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
