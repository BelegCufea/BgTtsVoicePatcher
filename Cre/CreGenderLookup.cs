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
///
/// The name-matching heuristic - strip a leading "B", strip a trailing "J"/"P", strip
/// trailing digits, then try dropping a trailing "A" or "E" too, finally falling back
/// to a wildcard glob - is a best-effort match against this specific BGEET install's
/// naming conventions, not a guaranteed-correct one. Expect the occasional wrong match;
/// it traded perfect accuracy for not having to hand-curate ~2000 names.
/// </summary>
public sealed class CreGenderLookup
{
    private readonly string _creDirectory;
    private readonly Dictionary<string, CreInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CreGenderLookup(string creDirectory)
    {
        if (!Directory.Exists(creDirectory))
            throw new DirectoryNotFoundException($"CRE directory not found: '{creDirectory}'");

        _creDirectory = creDirectory;
    }

    /// <summary>Just the gender, for the live --cre-dir path in 'generate' that
    /// doesn't need a display name.</summary>
    public Gender Resolve(string speakerName) => ResolveInfo(speakerName)?.Gender ?? Gender.Unknown;

    /// <summary>The full CreInfo (gender + name StrRef), for 'speakers' to build the
    /// display-name-grouped speaker-names.json.</summary>
    public CreInfo? ResolveInfo(string speakerName)
    {
        if (_cache.TryGetValue(speakerName, out var cached))
            return cached;

        var crePath = FindCreFile(speakerName);
        var info = crePath is null ? null : ReadCreInfo(crePath);

        _cache[speakerName] = info;
        return info;
    }

    private string? FindCreFile(string scriptName)
    {
        var baseName = scriptName;

        if (baseName.Length > 0 && baseName[0] == 'B')
            baseName = baseName[1..];

        if (baseName.Length > 0 && (baseName[^1] == 'J' || baseName[^1] == 'P'))
            baseName = baseName[..^1];

        var baseNoDigits = Regex.Replace(baseName, @"\d+$", "");

        var candidates = new List<string> { scriptName, baseName, baseNoDigits };

        if (baseNoDigits.Length > 0 && baseNoDigits[^1] is 'A' or 'E')
            candidates.Add(baseNoDigits[..^1]);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Windows file lookups are case-insensitive, so one extension check covers
            // both ".CRE" and ".cre" on disk.
            var path = Path.Combine(_creDirectory, candidate + ".cre");
            if (File.Exists(path))
                return path;
        }

        // Wildcard fallback: first alphabetical match starting with the stripped base.
        return Directory.EnumerateFiles(_creDirectory, baseNoDigits + "*.cre")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static CreInfo ReadCreInfo(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new CreInfo(ReadGender(bytes), ReadNameStrRef(bytes));
    }

    private static Gender ReadGender(byte[] bytes)
    {
        // EE CRE: Sex byte at offset 0x237. Very old (non-EE) CREs used 0x28 instead;
        // kept as a fallback even though this tool otherwise targets EE games.
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

    /// <summary>Long name (0x0008) if set, else Short name/tooltip (0x000c). A
    /// negative value (commonly -1) means "no name override" for that field.</summary>
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

