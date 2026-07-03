using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BgTtsVoicePatcher.State;

namespace BgTtsVoicePatcher.Cre;

/// <summary>Result of locating and reading one CRE file: its gender, plus a StrRef
/// for its in-game display name if the CRE has one (Long name preferred, falling back
/// to Short name/tooltip; null if neither is set).</summary>
public sealed record CreInfo(Gender Gender, int? NameStrRef);

/// <summary>
/// Resolves a DLG-derived speaker name to gender/name info by locating a matching
/// .CRE file (in a directory mass-exported via Near Infinity) and reading its Sex
/// byte and Long/Short name StrRefs.
/// </summary>
public sealed class CreGenderLookup
{
    private readonly string _creDirectory;
    private readonly Dictionary<string, CreInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    // In-memory index of all files in the CRE directory to prevent tens of thousands of disk I/O hits.
    private readonly HashSet<string> _creFileIndex;

    private static readonly Dictionary<string, string> HardcodedReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        { "BDORN", "DORN" },
        { "HAERDA", "HAER" },
        { "HAERD", "HAER" },
        { "HEXXAT", "OHHEX" },
        { "HEXXA", "OHHEX" },
        { "VALYGAR", "VALYG" },
        { "VALYGA", "VALYG" },
        { "YOSHIMOX", "YOSHI" },
        { "YOSHIMO", "YOSHI" },
        { "YOSHIM", "YOSHI" },
        { "EDWINW", "EDWIN"},
        { "IMOEM", "IMOEN" },
        { "IMRAE", "UDIMRAE" },
        { "KORGANA", "KORGAN" },
        { "KORGANF", "KORGAN" },
        { "MAIRYN", "RSPIRIT" },
        { "OHBNOOB", "NOOBER" },
        { "BAELOTHJ", "BAELOTH" },
        { "BAELOTHP", "BAELOTH" },
        { "BRANWJ", "BRANWE" },
        { "DELAINY", "DELAIN" },
        { "KIRINHAL", "KIRINH" },
        { "LUSETTE", "HARPASS" },
        { "MINCS", "MINSC" },
        { "MINSCZ", "MINSC" },
        { "NEERAZ", "NEERA" },
        { "SAFANZ", "SAFANA" },
        { "TIRLANA", "IRLANA" },
        { "VALYGARX", "VALYG" },
        { "VICONZ", "VICONI" },
        { "XANZ", "XAN" },
        { "YAGCON", "HGSLV" },
        { "RASAADZ", "RASAAD" },
    };

    private static readonly Dictionary<string, Gender> HardcodedGenderOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        { "EDWINW", Gender.Female },
        { "BDMKHIIJ", Gender.Female },
        { "BDMKHIIN", Gender.Female },
    };

    public CreGenderLookup(string creDirectory)
    {
        if (!Directory.Exists(creDirectory))
            throw new DirectoryNotFoundException($"CRE directory not found: '{creDirectory}'");

        _creDirectory = creDirectory;

        // Initialize the file index cache once on startup
        _creFileIndex = Directory.EnumerateFiles(_creDirectory, "*.cre")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }

    /// <summary>Just the gender, for the live --cre-dir path in 'generate' that
    /// doesn't need a display name.</summary>
    public Gender Resolve(string speakerName)
    {
        var gender = ResolveInfo(speakerName)?.Gender ?? Gender.Unknown;

        // If a base file was found, check if this specific speaker name requires a gender flip
        if (HardcodedGenderOverrides.TryGetValue(speakerName, out var overriddenGender))
        {
            gender = overriddenGender;
        }

        return gender;
    }

    /// <summary>The full CreInfo (gender + name StrRef), for 'speakers' to build the
    /// display-name-grouped speaker-names.json.</summary>
    public CreInfo? ResolveInfo(string speakerName)
    {
        if (_cache.TryGetValue(speakerName, out var cached))
            return cached;

        var crePath = FindCreFile(speakerName);
        var info = crePath is null ? null : ReadCreInfo(crePath);

        if (HardcodedGenderOverrides.TryGetValue(speakerName, out var overriddenGender))
        {
            if (info is not null)
                info = info with { Gender = overriddenGender };
            else
                info = new CreInfo(overriddenGender, null);
        }

        _cache[speakerName] = info;
        return info;
    }

    private string? FindCreFile(string scriptName)
    {
        // Local helper to execute the resolution chain for the current state of the string
        bool TryResolveCascade(string current, out string? resolvedPath)
        {
            // 1. Try direct match
            if (TryGetPath(current, out resolvedPath))
                return true;

            // 2. Strip trailing digits and try (e.g., VICONIA6 -> VICONIA)
            var noDigits = Regex.Replace(current, @"\d+$", "");
            if (TryGetPath(noDigits, out resolvedPath))
                return true;

            // 3. Strip trailing 'A' or 'E' and try
            if (noDigits.Length > 0 && noDigits[^1] is 'A' or 'E')
            {
                var noDigitsNoSuffix = noDigits[..^1];
                if (TryGetPath(noDigitsNoSuffix, out resolvedPath))
                    return true;
            }

            // 4. Wildcard Fallback
            var wildcardMatch = _creFileIndex
                .Where(name => name.StartsWith(noDigits, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (wildcardMatch != null)
            {
                resolvedPath = Path.Combine(_creDirectory, wildcardMatch + ".cre");
                return true;
            }

            resolvedPath = null;
            return false;
        }

        // --- Step 1: Try original script name ---
        if (TryResolveCascade(scriptName, out var matchUnmodified)) return matchUnmodified;

        var baseName = scriptName;

        foreach (var kvp in HardcodedReplacements)
        {
            // Regex.Replace with RegexOptions.IgnoreCase handles case-insensitivity safely
            baseName = Regex.Replace(baseName, kvp.Key, kvp.Value);
        }

        if (TryResolveCascade(baseName, out var match)) return match;

        if (baseName.Length > 0 && (baseName[^1] == '_'))
        {
            baseName = baseName[..^1];
            if (TryResolveCascade(baseName, out match)) return match;
        }

        // --- Step 2: Strip "BD" or "TB" prefix ---
        if (baseName.StartsWith("BD", StringComparison.OrdinalIgnoreCase) ||
            baseName.StartsWith("TB", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[2..];
            if (TryResolveCascade(baseName, out match)) return match;
        }

        // --- Step 3: Strip "B" prefix ---
        if (baseName.Length > 0 && baseName[0] == 'B')
        {
            baseName = baseName[1..];
            if (TryResolveCascade(baseName, out match)) return match;
        }

        // --- Step 4: Strip trailing 'J', 'P', 'B', or 'S' suffix ---
        if (baseName.Length > 0 && (baseName[^1] == 'J' || baseName[^1] == 'P' || baseName[^1] == 'B' || baseName[^1] == 'S' || baseName[^1] == 'D'))
        {
            baseName = baseName[..^1];
            if (TryResolveCascade(baseName, out match)) return match;
        }

        // --- Step 5: Strip first digit and everything after it ---
        var strippedDigits = Regex.Replace(baseName, @"\d.*$", "");
        if (strippedDigits != baseName)
        {
            baseName = strippedDigits;
            if (TryResolveCascade(baseName, out match)) return match;
        }

        return null;
    }

    // Helper to verify existence via the index cache and return the final OS path
    private bool TryGetPath(string candidate, out string? fullPath)
    {
        if (!string.IsNullOrEmpty(candidate) && _creFileIndex.Contains(candidate))
        {
            fullPath = Path.Combine(_creDirectory, candidate + ".cre");
            return true;
        }
        fullPath = null;
        return false;
    }

    private static CreInfo ReadCreInfo(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new CreInfo(ReadGender(bytes), ReadNameStrRef(bytes));
    }

    private static Gender ReadGender(byte[] bytes)
    {
        var offset = bytes.Length > 0x237 ? 0x237 : bytes.Length > 0x28 ? 0x28 : -1;
        if (offset < 0)
            return Gender.Unknown;

        return bytes[offset] switch
        {
            1 => Gender.Male,
            2 => Gender.Female,
            _ => Gender.Unknown
        };
    }

    private static int? ReadNameStrRef(byte[] bytes)
    {
        if (bytes.Length < 0x10)
            return null;

        var longName = BitConverter.ToInt32(bytes, 0x0008);
        if (longName >= 0)
            return longName;

        var shortName = BitConverter.ToInt32(bytes, 0x000c);
        return shortName >= 0 ? shortName : null;
    }
}