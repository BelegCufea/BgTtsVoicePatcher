using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BgTtsVoicePatcher.Config;
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
/// Name replacements and gender overrides come from PatcherConfig rather than
/// being hardcoded, so they can be edited without recompiling.
/// </summary>
public sealed class CreGenderLookup
{
    private readonly string _creDirectory;
    private readonly Dictionary<string, CreInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameReplacements;
    private readonly Dictionary<string, Gender> _genderOverrides;
    private readonly Dictionary<string, string> _dlgToCreIndex;

    // In-memory index of all files in the CRE directory to prevent tens of thousands of disk I/O hits.
    private readonly HashSet<string> _creFileIndex;

    public CreGenderLookup(string creDirectory, PatcherConfig config,
        IProgress<string>? progress = null, IProgress<(int Done, int Total, TimeSpan RemainingTime)>? numericProgress = null)
    {
        if (!Directory.Exists(creDirectory))
            throw new DirectoryNotFoundException($"CRE directory not found: '{creDirectory}'");

        _creDirectory = creDirectory;
        _nameReplacements = config.CreNameReplacements;
        _genderOverrides = config.GetParsedGenderOverrides();

        _creFileIndex = Directory.EnumerateFiles(_creDirectory, "*.cre")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        _dlgToCreIndex = BuildDlgToCreIndex(_creDirectory, _creFileIndex, progress, numericProgress);
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

        if (_genderOverrides.TryGetValue(speakerName, out var overriddenGender))
        {
            if (info is not null)
                info = info with { Gender = overriddenGender };
            else
                info = new CreInfo(overriddenGender, null);
        }

        _cache[speakerName] = info;
        return info;
    }


    /// <summary>Cross-references every CRE's own embedded Dialog file resref (CRE V1.0
    /// offset 0x02cc, an 8-byte fixed-width field) back to that CRE's basename. Built
    /// once at construction time so FindCreFile can do an authoritative lookup instead
    /// of guessing - a CRE's own DLG field is ground truth, not a heuristic.</summary>
    private static Dictionary<string, string> BuildDlgToCreIndex(
        string creDirectory, HashSet<string> creFileIndex,
        IProgress<string>? progress, IProgress<(int Done, int Total, TimeSpan RemainingTime)>? numericProgress)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ordered = creFileIndex.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var total = ordered.Count;
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < total; i++)
        {
            var creBaseName = ordered[i];
            var path = Path.Combine(creDirectory, creBaseName + ".cre");
            try
            {
                var dlgRef = ReadDialogResRef(path);
                if (!string.IsNullOrEmpty(dlgRef))
                    index.TryAdd(dlgRef, creBaseName);
            }
            catch
            {
                // Skip unreadable/corrupt files rather than failing the whole index build.
            }

            if (numericProgress is not null)
            {
                var elapsed = stopwatch.Elapsed;
                var done = i + 1;
                var remaining = TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / done * (total - done));
                numericProgress.Report((done, total, remaining));
            }

            if (stopwatch.ElapsedMilliseconds >= 500 || i == total - 1)
            {
                progress?.Report($"Indexing CRE files: {i + 1} / {total}");
                stopwatch.Restart();
            }
        }

        return index;
    }
    private static string? ReadDialogResRef(string path)
    {
        const int dialogFieldOffset = 0x02cc;
        const int dialogFieldLength = 8;

        using var stream = File.OpenRead(path);
        if (stream.Length < dialogFieldOffset + dialogFieldLength)
            return null;

        stream.Seek(dialogFieldOffset, SeekOrigin.Begin);
        var buffer = new byte[dialogFieldLength];
        if (stream.Read(buffer, 0, dialogFieldLength) < dialogFieldLength)
            return null;

        // Resref fields are fixed 8-byte, null-padded - NOT necessarily null-terminated
        // if the name uses the full 8 characters, so trim trailing nulls rather than
        // reading until the first one (which could run past the field into adjacent data).
        var text = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
        return text.Length == 0 ? null : text;
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

        var baseName = scriptName;

        // Highest priority: an exact, authoritative match via the CRE's own embedded
        // Dialog file reference. Not a guess - skip all heuristic stripping entirely
        // when this hits.
        if (_dlgToCreIndex.TryGetValue(baseName, out var creBaseName))
            return Path.Combine(_creDirectory, creBaseName + ".cre");

        // Apply name replacements from config before any other stripping
        foreach (var kvp in _nameReplacements)
        {
            // Regex.Replace with RegexOptions.IgnoreCase handles case-insensitivity safely
            baseName = Regex.Replace(baseName, kvp.Key, kvp.Value);
        }

        // --- Step 1: Try original script name ---
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