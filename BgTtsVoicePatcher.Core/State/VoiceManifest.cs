using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BgTtsVoicePatcher.State;

public enum VoiceEntryStatus
{
    Patched,
    Failed
}

public sealed class VoiceManifestEntry
{
    public int StrRef { get; set; }
    public string ResRef { get; set; } = string.Empty;
    public string TextHash { get; set; } = string.Empty;
    public string? Voice { get; set; }
    public VoiceEntryStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Tracks what's already been generated/patched so re-running the tool can skip work
/// that's already done (or re-synthesize only entries whose source text changed, or
/// only retry ones that previously failed). Written atomically via a temp-file rename
/// so a crash mid-save can't leave a corrupt manifest behind.
/// </summary>
public sealed class VoiceManifest
{
    private readonly Dictionary<int, VoiceManifestEntry> _entries;
    private readonly string _path;

    private VoiceManifest(string path, Dictionary<int, VoiceManifestEntry> entries)
    {
        _path = path;
        _entries = entries;
    }

    public static VoiceManifest LoadOrCreate(string path)
    {
        if (!File.Exists(path))
            return new VoiceManifest(path, new Dictionary<int, VoiceManifestEntry>());

        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<VoiceManifestEntry>>(json) ?? new List<VoiceManifestEntry>();
        return new VoiceManifest(path, list.ToDictionary(e => e.StrRef));
    }

    public VoiceManifestEntry? Get(int strRef) => _entries.GetValueOrDefault(strRef);

    public void Set(VoiceManifestEntry entry)
    {
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        _entries[entry.StrRef] = entry;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(
            _entries.Values.OrderBy(e => e.StrRef).ToList(),
            new JsonSerializerOptions { WriteIndented = true });

        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    public IReadOnlyCollection<VoiceManifestEntry> Entries => _entries.Values;
}
