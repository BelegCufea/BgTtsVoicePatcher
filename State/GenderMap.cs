using System.Text.Json;

namespace BgTtsVoicePatcher.State;

public enum Gender { Unknown, Male, Female }

/// <summary>
/// Optional StrRef -> Gender lookup so a male/female SAPI voice can be picked per
/// line. dialog.tlk has no speaker or gender field at all, so this is intentionally
/// just a small hand-maintained JSON file, e.g.:
///   { "12345": "F", "67890": "M" }
/// Use the StrRef numbers shown by the 'scan' command as you identify who's speaking.
/// Anything not listed resolves to Gender.Unknown and falls back to --voice.
/// </summary>
public sealed class GenderMap
{
    private readonly Dictionary<int, Gender> _map;

    private GenderMap(Dictionary<int, Gender> map) => _map = map;

    public static GenderMap Empty { get; } = new(new Dictionary<int, Gender>());

    public static GenderMap Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Empty;

        if (!File.Exists(path))
            throw new FileNotFoundException($"Gender map not found at '{path}'.");

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                  ?? new Dictionary<string, string>();

        var map = new Dictionary<int, Gender>();
        foreach (var (strRefText, genderText) in raw)
        {
            if (!int.TryParse(strRefText, out var strRef))
                throw new FormatException($"Gender map key '{strRefText}' is not a valid StrRef number.");

            map[strRef] = genderText.Trim().ToUpperInvariant() switch
            {
                "M" or "MALE" => Gender.Male,
                "F" or "FEMALE" => Gender.Female,
                _ => throw new FormatException($"Gender map value '{genderText}' for StrRef {strRef} must be M or F.")
            };
        }

        return new GenderMap(map);
    }

    public Gender Get(int strRef) => _map.GetValueOrDefault(strRef, Gender.Unknown);
}
