using System.Linq;
using System.Text.RegularExpressions;

namespace BgTtsVoicePatcher.Text;

/// <summary>
/// Strips Infinity Engine dialogue markup/tokens from raw TLK text so it reads
/// naturally when spoken aloud. The real game substitutes tokens like &lt;CHARNAME&gt;
/// with the player's actual name at display time; since pre-baked audio can't do that,
/// each known token is replaced with a fixed stand-in word instead (configurable).
/// </summary>
public sealed class DialogTextCleaner
{
    private static readonly Regex UnknownTagPattern = new(@"<[^<>]{1,40}>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _tokenMap;

    public DialogTextCleaner(Dictionary<string, string>? tokenMapOverrides = null, string charNameReplacement = "friend")
    {
        _tokenMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHARNAME"] = charNameReplacement,
            ["GABBER"] = "you",
            ["PRO_HESHE"] = "they",
            ["PRO_HIMHER"] = "them",
            ["PRO_HISHER"] = "their",
            ["PRO_MANWOMAN"] = "person",
            ["PRO_GIRLBOY"] = "kid",
            ["PRO_BROSIS"] = "sibling",
            ["PRO_RACE"] = "person",
        };

        if (tokenMapOverrides is not null)
        {
            foreach (var (key, value) in tokenMapOverrides)
                _tokenMap[key] = value;
        }
    }

    public string Clean(string rawText)
    {
        var text = rawText;

        foreach (var (token, replacement) in _tokenMap)
            text = text.Replace($"<{token}>", replacement, StringComparison.OrdinalIgnoreCase);

        // Anything still in angle brackets is an unrecognised token (e.g. one added by
        // another mod) - drop the brackets rather than let the TTS engine read them
        // literally as "less than ... greater than".
        text = UnknownTagPattern.Replace(text, string.Empty);

        text = text.Replace('\r', ' ').Replace('\n', ' ');
        text = WhitespacePattern.Replace(text, " ").Trim();

        return text;
    }

    /// <summary>True if there's enough real content left to bother synthesizing
    /// (filters out blank placeholder entries, stray punctuation, etc).</summary>
    public bool LooksSpeakable(string cleanedText, int minLength) =>
        cleanedText.Length >= minLength && cleanedText.Any(char.IsLetterOrDigit);
}
