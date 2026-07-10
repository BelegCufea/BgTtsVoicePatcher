using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BgTtsVoicePatcher.Gui.Engine;

/// <summary>Finds candidate dialog.tlk files under a game directory's `lang\` folder,
/// so the GUI can offer them in a picklist instead of the user typing a path.</summary>
public static class TlkLocator
{
    /// <summary>Returns every `lang\&lt;subfolder&gt;\dialog.tlk` that actually exists,
    /// ordered alphabetically by subfolder name. Empty list if there's no `lang`
    /// folder at all, or the game directory doesn't exist.</summary>
    public static List<string> FindDialogTlkFiles(string gameDir)
    {
        var langRoot = Path.Combine(gameDir, "lang");
        if (!Directory.Exists(langRoot))
            return new List<string>();

        return Directory.EnumerateDirectories(langRoot)
            .Select(dir => Path.Combine(dir, "dialog.tlk"))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Just the language subfolder name for display, e.g. "en_US" from
    /// ".../lang/en_US/dialog.tlk".</summary>
    public static string GetLangLabel(string tlkPath) =>
        Path.GetFileName(Path.GetDirectoryName(tlkPath)) ?? tlkPath;
}
