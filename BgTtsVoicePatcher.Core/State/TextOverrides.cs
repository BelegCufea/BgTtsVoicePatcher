using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BgTtsVoicePatcher.State;

/// <summary>
/// Persists per-StrRef text overrides - final, hand-edited wording that should be
/// used for TTS synthesis instead of the auto-cleaned (token-replaced, phonetic-regex-
/// applied) text. Stored as a flat StrRef -> text JSON map, typically named
/// text-overrides.json beside dialog.tlk. Checked first at generate time; falls back
/// to the normal DialogTextCleaner output for any StrRef with no override entry.
/// </summary>
public static class TextOverrides
{
    public static Dictionary<int, string> Load(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<int, string>();

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                  ?? new Dictionary<string, string>();

        return raw.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
    }

    public static void Save(string path, IReadOnlyDictionary<int, string> overrides)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            overrides.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json);
    }
}
