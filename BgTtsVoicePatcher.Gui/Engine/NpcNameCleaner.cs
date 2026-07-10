using System;
using System.Text.RegularExpressions;

namespace BgTtsVoicePatcher.Gui.Engine;

/// <summary>
/// Strips the color markup some NPC-color mods embed directly in a creature's
/// name StrRef, e.g. "^0xFF698748Haer'Dalis^-" -> "Haer'Dalis". This is purely a
/// display-cleanup concern for the Speaker Review grid's "in-game name" column -
/// it does not touch dialogue text, dialog.tlk, or anything else.
/// </summary>
public static class NpcNameCleaner
{
    private static readonly Regex ColorOpenPattern = new(@"\^0x[0-9A-Fa-f]{6,8}", RegexOptions.Compiled);
    private static readonly Regex ColorClosePattern = new(@"\^-", RegexOptions.Compiled);

    public static string? Clean(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var cleaned = ColorOpenPattern.Replace(name, string.Empty);
        cleaned = ColorClosePattern.Replace(cleaned, string.Empty);
        cleaned = cleaned.Trim();

        return cleaned.Length == 0 ? name : cleaned;
    }
}
