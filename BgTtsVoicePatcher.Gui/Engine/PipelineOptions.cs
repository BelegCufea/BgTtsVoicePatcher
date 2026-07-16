using System;
using System.IO;
using System.Text;

namespace BgTtsVoicePatcher.Gui.Engine;

/// <summary>
/// All settings for a run, gathered from the GUI wizard. Driven by an explicit
/// TlkPath (chosen via the Game &amp; TLK step's scan/browse) rather than derived
/// purely from GameDir+Lang, since the user picks the actual dialog.tlk file
/// directly - GameDir is kept mainly to default OverrideDir and for display.
/// </summary>
public sealed class PipelineOptions
{
    public required string GameDir { get; init; }
    public required string TlkPath { get; init; }

    /// <summary>Overrides the default `&lt;GameDir&gt;\override` location if set.</summary>
    public string? OverrideDirOverride { get; init; }

    /// <summary>Directory of .dlg files (Near Infinity mass-export recommended).
    /// Defaults to the override folder if not set.</summary>
    public string? DlgDir { get; init; }

    /// <summary>Directory of .CRE files (Near Infinity mass-export). Enables
    /// automatic gender resolution via each speaker's Sex byte.</summary>
    public string? CreDir { get; init; }

    /// <summary>Path to patcher-config.json actually in effect for this run
    /// (resolved via ConfigService: beside the TLK, or the bundled default).</summary>
    public string? ConfigPath { get; init; }

    public string? VoiceName { get; init; }
    public string? MaleVoice { get; init; }
    public string? FemaleVoice { get; init; }

    public bool UseOgg { get; init; }
    public string FfmpegPath { get; init; } = "ffmpeg";
    public int OggQuality { get; init; } = 2;

    public int Rate { get; init; }
    public int Volume { get; init; } = 100;
    public string Prefix { get; init; } = "TS";
    public int Limit { get; init; } = int.MaxValue;
    public bool DryRun { get; init; }

    public Encoding Encoding { get; init; } = Encoding.UTF8;
    public int MinLength { get; init; } = 2;

    /// <summary>Skip the 'speakers' step even if speaker-strrefs.json /
    /// speaker-names.json are missing - the GUI sets this once the Speakers step
    /// has already been run explicitly, so the final Run doesn't redo it.</summary>
    public bool SkipSpeakersStep { get; init; }

    // ---- derived paths ------------------------------------------------------

    public string OverrideDir => string.IsNullOrWhiteSpace(OverrideDirOverride)
        ? Path.Combine(GameDir, "override")
        : OverrideDirOverride;

    public string LangDir => Path.GetDirectoryName(TlkPath) ?? GameDir;
    public string SpeakerMapPath => Path.Combine(LangDir, "speaker-strrefs.json");
    public string SpeakerNamesPath => Path.Combine(LangDir, "speaker-names.json");
    public string SpeakerStatsPath => Path.Combine(LangDir, "speaker-stats.json");
    public string SpeakerUnmatchedPath => Path.Combine(LangDir, "speaker-unmatched.txt");
    public string ReportPath => Path.Combine(LangDir, "dialog-report.csv");
    public string TextOverridesPath => Path.Combine(LangDir, "text-overrides.json");
    public string EffectiveDlgDir => string.IsNullOrWhiteSpace(DlgDir) ? OverrideDir : DlgDir;
    public string EffectiveCreDir => string.IsNullOrWhiteSpace(CreDir) ? OverrideDir : CreDir;
    public string ManifestPath => Path.Combine(LangDir, "tts-manifest.json");

    /// <summary>When set, generate processes exactly these StrRefs regardless of voiced
    /// state or Limit, mirroring the CLI's --strrefs. Null = normal selection.</summary>
    public IReadOnlySet<int>? StrRefFilter { get; init; }
}

public sealed record PipelineProgress(int Done, int Total, int Generated, int Reused, int Failed, TimeSpan? RemainingTime = null, string Phase = "Generating");

public sealed record PipelineResult(bool Success, int Generated, int Reused, int Failed, string Message);
