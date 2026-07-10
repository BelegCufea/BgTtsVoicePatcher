using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BgTtsVoicePatcher.Config;
using BgTtsVoicePatcher.Gui.Models;

namespace BgTtsVoicePatcher.Gui.Engine;

/// <summary>
/// Resolves, loads, and saves patcher-config.json for the GUI's config-editing step.
/// Resolution order matches what the user asked for: look beside the chosen
/// dialog.tlk first, and only fall back to the copy bundled next to the
/// executable if there isn't one there yet. Saving always writes beside the TLK,
/// regardless of where the config was originally loaded from - editing effectively
/// "forks" a per-install config the first time you save, same as speaker-*.json
/// already does for that install.
/// </summary>
public static class ConfigService
{
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public static string ResolveConfigPath(string tlkPath)
    {
        var tlkDir = Path.GetDirectoryName(tlkPath) ?? "";
        var besideTlk = Path.Combine(tlkDir, "patcher-config.json");
        return File.Exists(besideTlk) ? besideTlk : PatcherConfig.DefaultConfigPath();
    }

    public static string GetSavePathFor(string tlkPath) =>
        Path.Combine(Path.GetDirectoryName(tlkPath) ?? "", "patcher-config.json");

    public static PatcherConfig Load(string path) => PatcherConfig.Load(path);

    /// <summary>Builds a brand-new PatcherConfig from the GUI's editable row
    /// collections and writes it to disk. All PatcherConfig properties are
    /// init-only, so a fresh instance (rather than mutating the loaded one) is
    /// the correct way to apply edits - and since every property carries a
    /// [JsonPropertyName] attribute matching the file format exactly, serializing
    /// the instance directly round-trips correctly with PatcherConfig.Load.</summary>
    public static void Save(
        string path,
        string pcName, string pcRace, string pcGender,
        IEnumerable<KeyValueRow> creNameReplacements,
        IEnumerable<KeyValueRow> genderOverrides,
        IEnumerable<GenderTokenRow> genderTokens,
        IEnumerable<TokenRow> identityTokens,
        IEnumerable<PhoneticRuleRow> phoneticRules)
    {
        var config = new PatcherConfig
        {
            PcName = pcName,
            PcRace = pcRace,
            PcGender = pcGender,
            CreNameReplacements = creNameReplacements
                .Where(r => !string.IsNullOrWhiteSpace(r.Key))
                .ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase),
            GenderOverrides = genderOverrides
                .Where(r => !string.IsNullOrWhiteSpace(r.Key))
                .ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase),
            GenderTokens = genderTokens
                .Where(r => !string.IsNullOrWhiteSpace(r.Token))
                .ToDictionary(
                    r => r.Token,
                    r => new GenderTokenValues(r.Male, r.Female, r.Neutral),
                    StringComparer.OrdinalIgnoreCase),
            IdentityTokens = identityTokens
                .Select(r => r.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList(),
            PhoneticRules = phoneticRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .Select(r => new PhoneticRule
                {
                    Pattern = r.Pattern,
                    Replacement = r.Replacement,
                    Comment = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment
                })
                .ToList()
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(config, SaveOptions));
    }
}
