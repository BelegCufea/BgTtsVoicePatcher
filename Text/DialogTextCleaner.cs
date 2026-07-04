using System.Linq;
using System.Text.RegularExpressions;
using BgTtsVoicePatcher.Config;

namespace BgTtsVoicePatcher.Text;

/// <summary>
/// Strips Infinity Engine dialogue markup/tokens from raw TLK text so it reads
/// naturally when spoken aloud. All substitution tables and PC identity values come
/// from PatcherConfig - nothing is hardcoded or passed via CLI any more.
/// </summary>
public sealed class DialogTextCleaner
{
    private static readonly Regex UnknownTagPattern = new(@"<[^<>]{1,40}>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _tokenMap;
    private readonly List<(Regex Pattern, string Replacement)> _phoneticRules;

    public DialogTextCleaner(PatcherConfig config)
    {
        _phoneticRules = config.GetCompiledPhoneticRules();
        _tokenMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Gender-sensitive tokens: pick the right side based on pcGender from config.
        foreach (var (token, values) in config.GenderTokens)
            _tokenMap[token] = values.Pick(config.PcGender);

        // Identity tokens: name- or race-bearing tokens get the fixed config values.
        foreach (var token in config.IdentityTokens)
        {
            _tokenMap[token] = token.Contains("RACE", StringComparison.OrdinalIgnoreCase)
                ? config.PcRace
                : config.PcName;
        }

        // GABBER always maps to the PC name as a safety net even if omitted from config.
        _tokenMap.TryAdd("GABBER", config.PcName);
    }

    public string Clean(string rawText)
    {
        var text = rawText;

        foreach (var (token, replacement) in _tokenMap)
            text = text.Replace($"<{token}>", replacement, StringComparison.OrdinalIgnoreCase);

        // Anything still in angle brackets is an unrecognised token - strip the brackets.
        text = UnknownTagPattern.Replace(text, string.Empty);

        // Phonetic rules from config, applied in order.
        foreach (var (pattern, replacement) in _phoneticRules)
            text = pattern.Replace(text, replacement);

        text = text.Replace('\r', ' ').Replace('\n', ' ');
        text = WhitespacePattern.Replace(text, " ").Trim();

        return text;
    }

    /// <summary>True if there's enough real content left to bother synthesizing.</summary>
    public bool LooksSpeakable(string cleanedText, int minLength) =>
        cleanedText.Length >= minLength && cleanedText.Any(char.IsLetterOrDigit);
}
