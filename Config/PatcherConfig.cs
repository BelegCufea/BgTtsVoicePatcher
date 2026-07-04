using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BgTtsVoicePatcher.State;

namespace BgTtsVoicePatcher.Config;

/// <summary>One phonetic find/replace rule. Both Pattern and Replacement are
/// .NET regular expression syntax.</summary>
public sealed class PhoneticRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; init; } = string.Empty;

    [JsonPropertyName("replacement")]
    public string Replacement { get; init; } = string.Empty;

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

/// <summary>Male, female, and neutral substitution strings for a single gender-sensitive token.</summary>
public sealed class GenderTokenValues
{
    [JsonPropertyName("male")]
    public string Male { get; init; } = string.Empty;

    [JsonPropertyName("female")]
    public string Female { get; init; } = string.Empty;

    [JsonPropertyName("neutral")]
    public string Neutral { get; init; } = string.Empty;

    [JsonConstructor]
    public GenderTokenValues() { }

    public GenderTokenValues(string male, string female, string neutral)
    {
        Male = male;
        Female = female;
        Neutral = neutral;
    }

    public string Pick(string pcGender) => pcGender.ToLowerInvariant() switch
    {
        "female" or "f" => Female,
        "neutral" or "n" => Neutral,
        _ => Male
    };
}

/// <summary>
/// All user-configurable settings. Loaded from patcher-config.json next to the
/// executable (overridable via --config). The file is required - if not found a
/// clear error is shown pointing to the expected path.
/// </summary>
public sealed class PatcherConfig
{
    /// <summary>Replacement word for the PC's name tokens (&lt;CHARNAME&gt;, &lt;GABBER&gt;).</summary>
    [JsonPropertyName("pcName")]
    public string PcName { get; init; } = "friend";

    /// <summary>Replacement word for race tokens (&lt;RACE&gt;, &lt;PRO_RACE&gt;).</summary>
    [JsonPropertyName("pcRace")]
    public string PcRace { get; init; } = "human";

    /// <summary>PC gender for gender-sensitive tokens: "male", "female", or "neutral".</summary>
    [JsonPropertyName("pcGender")]
    public string PcGender { get; init; } = "neutral";

    /// <summary>Maps DLG system names to CRE basenames when the normal heuristic fails.</summary>
    [JsonPropertyName("creNameReplacements")]
    public Dictionary<string, string> CreNameReplacements { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-system-name gender overrides that bypass CRE lookup entirely.</summary>
    [JsonPropertyName("genderOverrides")]
    public Dictionary<string, string> GenderOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replacements for PC gender-sensitive tokens. Key is the token inner name
    /// (no angle brackets); each value has male/female/neutral sides.</summary>
    [JsonPropertyName("genderTokens")]
    public Dictionary<string, GenderTokenValues> GenderTokens { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Token inner names replaced by pcName (default) or pcRace (if name
    /// contains "RACE").</summary>
    [JsonPropertyName("identityTokens")]
    public List<string> IdentityTokens { get; init; } = new();

    /// <summary>Phonetic find/replace rules applied after all token substitution, in order.</summary>
    [JsonPropertyName("phoneticRules")]
    public List<PhoneticRule> PhoneticRules { get; init; } = new();

    // ---- loading -----------------------------------------------------------

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static PatcherConfig Load(string? explicitPath)
    {
        var path = explicitPath ?? DefaultConfigPath();

        if (!File.Exists(path))
        {
            var message = explicitPath is not null
                ? $"Config file not found at '{path}'."
                : $"patcher-config.json not found at '{path}'. " +
                  $"Place patcher-config.json next to the executable or pass --config <path>.";
            throw new FileNotFoundException(message);
        }

        try
        {
            return JsonSerializer.Deserialize<PatcherConfig>(File.ReadAllText(path), SerializerOptions)
                   ?? throw new InvalidDataException("Config file deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Config file '{path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    public static string DefaultConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "patcher-config.json");

    // ---- derived lookups ---------------------------------------------------

    public Dictionary<string, Gender> GetParsedGenderOverrides()
    {
        var result = new Dictionary<string, Gender>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in GenderOverrides)
        {
            result[name] = value.Trim().ToUpperInvariant() switch
            {
                "M" or "MALE"   => Gender.Male,
                "F" or "FEMALE" => Gender.Female,
                _ => Gender.Unknown
            };
        }
        return result;
    }

    public List<(Regex Pattern, string Replacement)> GetCompiledPhoneticRules()
    {
        var result = new List<(Regex, string)>();
        foreach (var rule in PhoneticRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
                continue;
            try
            {
                result.Add((new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase), rule.Replacement));
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"  Warning: phonetic rule pattern '{rule.Pattern}' is invalid regex: {ex.Message} — skipped.");
            }
        }
        return result;
    }
}
