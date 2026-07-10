using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BgTtsVoicePatcher.State;

/// <summary>
/// Loads the hand-edited speaker-names.json produced by 'speakers':
///   { "Display Name": { "SYSNAME1": "M", "SYSNAME2": "F", "SYSNAME3": null }, ... }
/// Flattened into systemName -> Gender and systemName -> display name lookups - the
/// nesting in the file is purely for human readability/editing convenience, lookups
/// always happen by the DLG-derived system name.
/// </summary>
public sealed class SpeakerGenderMap
{
    private readonly Dictionary<string, Gender> _genderBySystemName;
    private readonly Dictionary<string, string> _displayNameBySystemName;

    private SpeakerGenderMap(Dictionary<string, Gender> genderBySystemName, Dictionary<string, string> displayNameBySystemName)
    {
        _genderBySystemName = genderBySystemName;
        _displayNameBySystemName = displayNameBySystemName;
    }

    public static SpeakerGenderMap Empty { get; } =
        new(new Dictionary<string, Gender>(), new Dictionary<string, string>());

    public static SpeakerGenderMap Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Empty;

        if (!File.Exists(path))
            throw new FileNotFoundException($"Speaker names file not found at '{path}'.");

        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, Dictionary<string, string?>>();

        var genderMap = new Dictionary<string, Gender>(StringComparer.OrdinalIgnoreCase);
        var displayNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (displayName, group) in raw)
        {
            foreach (var (systemName, genderText) in group)
            {
                genderMap[systemName] = genderText?.Trim().ToUpperInvariant() switch
                {
                    "M" or "MALE" => Gender.Male,
                    "F" or "FEMALE" => Gender.Female,
                    _ => Gender.Unknown
                };
                displayNameMap[systemName] = displayName;
            }
        }

        return new SpeakerGenderMap(genderMap, displayNameMap);
    }

    public Gender Get(string systemName) => _genderBySystemName.GetValueOrDefault(systemName, Gender.Unknown);

    /// <summary>The group key (display name) a system name was filed under, if any -
    /// note this is whatever was in the file, which falls back to the system name
    /// itself when 'speakers' couldn't resolve a real display name, so this can
    /// return something that isn't actually a "real" name.</summary>
    public string? GetDisplayName(string systemName) => _displayNameBySystemName.GetValueOrDefault(systemName);
}

