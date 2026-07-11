using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BgTtsVoicePatcher.Audio;
using BgTtsVoicePatcher.Config;
using BgTtsVoicePatcher.Cre;
using BgTtsVoicePatcher.Reporting;
using BgTtsVoicePatcher.ResRef;
using BgTtsVoicePatcher.Speakers;
using BgTtsVoicePatcher.Speech;
using BgTtsVoicePatcher.State;
using BgTtsVoicePatcher.Text;
using BgTtsVoicePatcher.Tlk;

namespace BgTtsVoicePatcher.Gui.Engine;

/// <summary>
/// Drives the same engine classes the CLI's 'speakers' / 'generate' / 'report'
/// commands use, but structured for the GUI: async, cancellable, and reporting
/// progress via callbacks instead of writing to Console. This is a deliberate,
/// intentional duplication of Program.cs's orchestration logic (not a refactor
/// of it) - the CLI's Run* methods are private, take a loosely-typed options
/// dictionary, and write straight to Console, none of which fits a GUI. Both
/// call into the identical underlying classes (TlkFile, TlkPatcher,
/// VoiceSynthesizer, SpeakerIndex, CreGenderLookup, etc), so behavior parity is
/// governed by those shared classes, not by this file mirroring Program.cs
/// line for line.
/// </summary>
public sealed class PipelineRunner
{
    /// <summary>Holds the resolved speaker-name/gender lookup data for one run,
    /// combining file-based (speaker-strrefs.json + speaker-names.json) and live
    /// (DLG scan + CRE lookup) sources, exactly like the CLI's SpeakerData.</summary>
    private sealed record SpeakerData(
        Dictionary<int, string> FileSpeakerMap,
        SpeakerGenderMap FileGenderMap,
        Dictionary<int, string> LiveSpeakerMap,
        CreGenderLookup? LiveCreLookup,
        HashSet<int> KnownSpeakers);

    public async Task<PipelineResult> RunAsync(
        PipelineOptions options,
        IProgress<string> log,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        return await Task.Run(() => RunCore(options, log, progress, ct), ct);
    }

    private PipelineResult RunCore(
        PipelineOptions options,
        IProgress<string> log,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        if (!Directory.Exists(options.OverrideDir))
            return Fail(log, $"override directory not found: '{options.OverrideDir}'");

        if (!File.Exists(options.TlkPath))
            return Fail(log, $"dialog.tlk not found: '{options.TlkPath}'.");

        var config = PatcherConfig.Load(options.ConfigPath);
        var encoding = options.Encoding;
        var cleaner = new DialogTextCleaner(config);

        log.Report($"Game directory : {options.GameDir}");
        log.Report($"Override       : {options.OverrideDir}");
        log.Report($"TLK            : {options.TlkPath}");
        log.Report($"DLG scan dir   : {options.EffectiveDlgDir}");
        if (!string.IsNullOrWhiteSpace(options.CreDir))
            log.Report($"CRE dir        : {options.CreDir}");
        log.Report("");

        ct.ThrowIfCancellationRequested();

        // -- Step 1: speakers (only if files missing and not explicitly skipped) --
        var haveSpeakerFiles = File.Exists(options.SpeakerMapPath) && File.Exists(options.SpeakerNamesPath);

        if (!haveSpeakerFiles && !options.SkipSpeakersStep)
        {
            log.Report("Speaker files not found — running speaker/gender resolution first...");
            log.Report("");
            RunSpeakersStep(options, config, encoding, cleaner, log, ct);
            log.Report("");
        }
        else if (haveSpeakerFiles)
        {
            log.Report($"Speaker files found in {options.LangDir} — skipping speaker resolution step.");
            log.Report("");
        }

        ct.ThrowIfCancellationRequested();

        // -- Step 2: generate ------------------------------------------------
        log.Report("Running generate...");
        log.Report("");
        var generateResult = RunGenerateStep(options, config, encoding, cleaner, log, progress, ct);

        ct.ThrowIfCancellationRequested();

        // -- Step 3: report ---------------------------------------------------
        log.Report("");
        log.Report("Writing report...");
        RunReportStep(options, config, encoding, cleaner, log);

        return generateResult;
    }

    // ---- Step: speakers ------------------------------------------------------

    private void RunSpeakersStep(
        PipelineOptions options, PatcherConfig config, Encoding encoding, DialogTextCleaner cleaner,
        IProgress<string> log, CancellationToken ct)
    {
        var dlgDir = options.EffectiveDlgDir;
        var tlk = TlkFile.Load(options.TlkPath, encoding);
        var candidates = GetTtsCandidates(tlk, cleaner, options.MinLength, int.MaxValue, parseAll: true);
        var candidateStrRefs = candidates.Select(c => c.Entry.StrRef).ToHashSet();

        log.Report($"Scanning *.dlg in: {dlgDir}");
        var scan = SpeakerIndex.Scan(dlgDir);
        log.Report($"  Files scanned: {scan.FilesScanned}, failed to parse: {scan.FilesFailed}");
        ct.ThrowIfCancellationRequested();

        var relevantMap = scan.StrRefToSpeaker
            .Where(kv => candidateStrRefs.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var relevantNames = relevantMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        CreGenderLookup? creLookup = string.IsNullOrWhiteSpace(options.CreDir)
            ? null
            : new CreGenderLookup(options.CreDir, config);
        var resolvedViaCre = 0;

        var speakerInfos = new List<SpeakerIndex.SpeakerInfo>();
        foreach (var systemName in relevantNames)
        {
            ct.ThrowIfCancellationRequested();
            var info = creLookup?.ResolveInfo(systemName);
            var gender = info?.Gender ?? Gender.Unknown;
            var displayName = ResolveDisplayName(info?.NameStrRef, tlk, cleaner);

            if (gender != Gender.Unknown)
                resolvedViaCre++;

            speakerInfos.Add(new SpeakerIndex.SpeakerInfo(systemName, displayName, gender));
        }

        SpeakerIndex.SaveStrRefMap(relevantMap, options.SpeakerMapPath);
        SpeakerIndex.SaveOrMergeNamesFile(speakerInfos, options.SpeakerNamesPath);
        var unresolvedLinesCount = SpeakerIndex.SaveOrMergeStatsFile(relevantMap, speakerInfos, options.SpeakerStatsPath);

        var unmatched = candidates
            .Where(c => !relevantMap.ContainsKey(c.Entry.StrRef))
            .OrderBy(c => c.Entry.StrRef)
            .ToList();

        File.WriteAllLines(options.SpeakerUnmatchedPath, unmatched.Select(c => $"[{c.Entry.StrRef}] {c.CleanedText}"));

        log.Report($"  TTS candidates with a known speaker: {relevantMap.Count} / {candidateStrRefs.Count}");
        log.Report($"  Distinct speaker names:    {relevantNames.Count}");
        if (creLookup is not null)
        {
            log.Report($"  Gendered via CRE Sex byte: {resolvedViaCre} / {relevantNames.Count}");
            log.Report($"  Total dialogue lines with unresolved gender: {unresolvedLinesCount}");
        }
        log.Report($"  No speaker found: {unmatched.Count} -> {options.SpeakerUnmatchedPath}");
        log.Report($"  Wrote: {options.SpeakerMapPath}");
        log.Report($"  Wrote/updated: {options.SpeakerNamesPath}");
        log.Report($"  Wrote stats: {options.SpeakerStatsPath}");
    }

    // ---- Step: generate --------------------------------------------------------

    private PipelineResult RunGenerateStep(
        PipelineOptions options, PatcherConfig config, Encoding encoding, DialogTextCleaner cleaner,
        IProgress<string> log, IProgress<PipelineProgress> progress, CancellationToken ct)
    {
        var tlk = TlkFile.Load(options.TlkPath, encoding);
        var sd = ResolveSpeakerData(options, config, log);
        var textOverrides = TextOverrides.Load(options.TextOverridesPath);

        var candidates = options.StrRefFilter is not null
            ? GetCandidatesForStrRefs(tlk, cleaner, options.MinLength, options.StrRefFilter, textOverrides, log)
            : GetTtsCandidates(tlk, cleaner, options.MinLength, options.Limit,
                requireKnownSpeaker: sd.KnownSpeakers, textOverrides: textOverrides);

        log.Report(options.StrRefFilter is not null
            ? $"{candidates.Count} line(s) selected explicitly ({options.StrRefFilter.Count} requested)."
            : $"{candidates.Count} unvoiced line(s) selected (limit={(options.Limit == int.MaxValue ? "none" : options.Limit.ToString())}).");

        using var synth = new VoiceSynthesizer(options.VoiceName, options.MaleVoice, options.FemaleVoice, options.Rate, options.Volume);
        var oggEncoder = options.UseOgg ? new OggEncoder(options.FfmpegPath, options.OggQuality) : null;

        var genderMap = GenderMap.Empty;
        VoiceManifest? manifest = null;

        var manifestPath = options.ManifestPath;

        if (!options.DryRun)
        {
            EnsureBackup(options.TlkPath);
            manifest = VoiceManifest.LoadOrCreate(manifestPath);
        }
        else
        {
            log.Report($"Dry run — writing WAV/OGG files to {options.OverrideDir} only.");
            log.Report("dialog.tlk will NOT be touched and no manifest will be written.");
        }

        using var patcher = options.DryRun ? null : TlkPatcher.Open(options.TlkPath);

        var generated = 0;
        var reused = 0;
        var failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (entry, cleaned) = candidates[i];
            var resRef = ResRefAllocator.ForStrRef(entry.StrRef, options.Prefix);
            var wavPath = System.IO.Path.Combine(options.OverrideDir, resRef + ".wav");
            var gender = ResolveGender(entry.StrRef, genderMap, sd);
            var voiceUsed = synth.VoiceNameFor(gender);
            var textHash = ComputeHash(cleaned + "|" + voiceUsed + "|" + (options.UseOgg ? "ogg" : "pcm"));

            try
            {
                var existing = !options.DryRun && options.StrRefFilter is null ? manifest!.Get(entry.StrRef) : null;
                var canReuse = existing is { } e && e.TextHash == textHash && File.Exists(wavPath);

                if (canReuse)
                {
                    reused++;
                }
                else
                {
                    synth.SpeakToWaveFile(cleaned, wavPath, gender);
                    oggEncoder?.EncodeInPlace(wavPath);
                    generated++;
                }

                if (!options.DryRun)
                {
                    patcher!.ApplySound(entry, resRef);
                    manifest!.Set(new VoiceManifestEntry
                    {
                        StrRef = entry.StrRef,
                        ResRef = resRef,
                        TextHash = textHash,
                        Voice = voiceUsed,
                        Status = VoiceEntryStatus.Patched
                    });
                }
            }
            catch (Exception ex)
            {
                failed++;
                log.Report($"  [{entry.StrRef}] FAILED: {ex.Message}");
                if (!options.DryRun)
                {
                    manifest!.Set(new VoiceManifestEntry
                    {
                        StrRef = entry.StrRef,
                        ResRef = resRef,
                        TextHash = textHash,
                        Voice = voiceUsed,
                        Status = VoiceEntryStatus.Failed,
                        Error = ex.Message
                    });
                }
            }

            var completedCount = i + 1;
            var remainingCount = candidates.Count - completedCount;

            TimeSpan remainingTime;
            if (completedCount > 0 && remainingCount > 0)
            {
                double msPerItem = sw.Elapsed.TotalMilliseconds / completedCount;
                remainingTime = TimeSpan.FromMilliseconds(msPerItem * remainingCount);
            }
            else
            {
                remainingTime = TimeSpan.Zero;
            }

            progress.Report(new PipelineProgress(completedCount, candidates.Count, generated, reused, failed, remainingTime));

            if (!options.DryRun && (i % 25 == 0 || i == candidates.Count - 1))
                manifest?.Save();
        }

        sw.Stop();

        if (options.DryRun)
        {
            log.Report("");
            log.Report($"Wrote {generated} file(s) ({failed} failed). Listen in {options.OverrideDir}, then run");
            log.Report("again without dry-run to patch dialog.tlk.");
        }
        else
        {
            log.Report("");
            log.Report("Done.");
            log.Report($"  Generated audio: {generated}");
            log.Report($"  Reused audio:    {reused}");
            log.Report($"  Failed:          {failed}");
            log.Report($"  Patched into:    {options.TlkPath}");
            log.Report($"  Manifest:        {manifestPath}");
        }

        var success = failed == 0;
        var message = success
            ? $"Completed: {generated} generated, {reused} reused."
            : $"Completed with {failed} failure(s): {generated} generated, {reused} reused.";

        return new PipelineResult(success, generated, reused, failed, message);
    }

    // ---- Step: report -----------------------------------------------------

    private void RunReportStep(
        PipelineOptions options, PatcherConfig config, Encoding encoding, DialogTextCleaner cleaner, IProgress<string> log)
    {
        var textOverrides = TextOverrides.Load(options.TextOverridesPath);
        var rows = BuildSpeakerReviewRowsInternal(options, config, encoding, cleaner, textOverrides, log);

        // CSV report keeps its existing shape (Core's DialogReportRow) - map the
        // richer review data down to it rather than changing that public contract.
        var reportRows = rows.Select(r => new DialogReportRow(
            r.StrRef, r.SystemName, r.RealName, r.Gender, r.HasSound, r.SoundResRef, r.SoundFileExists, r.RawText));

        DialogReportWriter.WriteCsv(reportRows, options.ReportPath);
        log.Report($"Wrote {rows.Count} row(s) to {options.ReportPath}.");
    }

    /// <summary>Builds the Speaker Review grid's rows: same speaker/gender resolution
    /// as 'report', plus the cleaned/spoken text (honoring any saved text override)
    /// and a color-code-stripped display name for the "in-game name" column.</summary>
    private List<SpeakerReviewRow> BuildSpeakerReviewRowsInternal(
        PipelineOptions options, PatcherConfig config, Encoding encoding, DialogTextCleaner cleaner,
        IReadOnlyDictionary<int, string> textOverrides, IProgress<string> log)
    {
        var tlk = TlkFile.Load(options.TlkPath, encoding);
        var sd = ResolveSpeakerData(options, config, log);

        var rows = new List<SpeakerReviewRow>();

        foreach (var entry in tlk.Entries)
        {
            if (!entry.HasText)
                continue;

            string? systemName = null;
            string? realName = null;
            var gender = Gender.Unknown;

            if (sd.FileSpeakerMap.TryGetValue(entry.StrRef, out var fileName))
            {
                systemName = fileName;
                gender = sd.FileGenderMap.Get(fileName);
                realName = sd.FileGenderMap.GetDisplayName(fileName);
            }
            else if (sd.LiveSpeakerMap.TryGetValue(entry.StrRef, out var liveName))
            {
                systemName = liveName;
                if (sd.LiveCreLookup is not null)
                {
                    var info = sd.LiveCreLookup.ResolveInfo(liveName);
                    gender = info?.Gender ?? Gender.Unknown;
                    realName = ResolveDisplayName(info?.NameStrRef, tlk, cleaner);
                }
            }

            if (realName is not null && systemName is not null && realName.Equals(systemName, StringComparison.Ordinal))
                realName = null;

            var soundResRef = entry.HasSound ? entry.SoundResRef : null;
            bool? soundFileExists = soundResRef is not null
                ? File.Exists(System.IO.Path.Combine(options.OverrideDir, soundResRef + ".wav"))
                : null;

            var isOverridden = textOverrides.TryGetValue(entry.StrRef, out var overrideText);
            var cleanedText = isOverridden ? overrideText! : cleaner.Clean(entry.Text);

            rows.Add(new SpeakerReviewRow(
                entry.StrRef,
                systemName,
                realName,
                gender switch { Gender.Male => "M", Gender.Female => "F", _ => "" },
                entry.HasSound,
                soundResRef,
                soundFileExists,
                entry.Text,
                cleanedText,
                isOverridden));
        }

        return rows;
    }

    // ---- public entry points for standalone GUI wizard steps ---------------

    /// <summary>Runs just the speaker/gender resolution step on its own (the GUI's
    /// "Run Speakers" button), without generate/report - lets the user review and
    /// correct speaker-names.json before committing to a full synthesis pass.</summary>
    public async Task RunSpeakersOnlyAsync(PipelineOptions options, IProgress<string> log, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(options.EffectiveDlgDir))
                throw new DirectoryNotFoundException($"DLG directory not found: '{options.EffectiveDlgDir}'");
            if (!File.Exists(options.TlkPath))
                throw new FileNotFoundException($"dialog.tlk not found: '{options.TlkPath}'");

            var config = PatcherConfig.Load(options.ConfigPath);
            var encoding = options.Encoding;
            var cleaner = new DialogTextCleaner(config);
            RunSpeakersStep(options, config, encoding, cleaner, log, ct);
        }, ct);
    }

    /// <summary>Builds the Speaker Review grid's rows directly (no CSV write) -
    /// reusable at any point once speaker files exist (or even without them,
    /// falling back to a live DLG/CRE scan).</summary>
    public async Task<List<SpeakerReviewRow>> BuildSpeakerReviewRowsAsync(
        PipelineOptions options, IProgress<string> log, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(options.TlkPath))
                throw new FileNotFoundException($"dialog.tlk not found: '{options.TlkPath}'");

            var config = PatcherConfig.Load(options.ConfigPath);
            var encoding = options.Encoding;
            var cleaner = new DialogTextCleaner(config);
            var textOverrides = TextOverrides.Load(options.TextOverridesPath);
            return BuildSpeakerReviewRowsInternal(options, config, encoding, cleaner, textOverrides, log);
        }, ct);
    }

    // ---- shared helpers (ported from Program.cs) ---------------------------

    private static SpeakerData ResolveSpeakerData(PipelineOptions options, PatcherConfig config, IProgress<string> log)
    {
        var fileSpeakerMap = File.Exists(options.SpeakerMapPath)
            ? SpeakerIndex.LoadStrRefMap(options.SpeakerMapPath)
            : new Dictionary<int, string>();
        var fileGenderMap = File.Exists(options.SpeakerNamesPath)
            ? SpeakerGenderMap.Load(options.SpeakerNamesPath)
            : SpeakerGenderMap.Empty;

        var hasSpeakerFile = fileSpeakerMap.Count > 0;
        var needCreLookup = !string.IsNullOrWhiteSpace(options.CreDir);
        var needDlgScan = !hasSpeakerFile;

        var liveSpeakerMap = new Dictionary<int, string>();
        CreGenderLookup? liveCreLookup = null;

        if (needDlgScan)
        {
            var reason = !hasSpeakerFile ? "no speaker map found" : "CRE dir requested";
            log.Report($"Scanning *.dlg in: {options.EffectiveDlgDir} ({reason})");
            var dlgScan = SpeakerIndex.Scan(options.EffectiveDlgDir);
            log.Report($"  Files scanned: {dlgScan.FilesScanned}, failed: {dlgScan.FilesFailed}");
            liveSpeakerMap = dlgScan.StrRefToSpeaker;
        }
        else
        {
            log.Report($"Using speaker map ({fileSpeakerMap.Count} entries) — DLG scan skipped.");
        }

        if (needCreLookup)
            liveCreLookup = new CreGenderLookup(options.CreDir!, config);

        var knownSpeakers = new HashSet<int>(fileSpeakerMap.Keys.Concat(liveSpeakerMap.Keys));
        if (knownSpeakers.Count == 0)
            log.Report("  Warning: no speaker data found. No lines will be selected.");

        return new SpeakerData(fileSpeakerMap, fileGenderMap, liveSpeakerMap, liveCreLookup, knownSpeakers);
    }

    private static Gender ResolveGender(int strRef, GenderMap genderMap, SpeakerData sd)
    {
        var gender = genderMap.Get(strRef);
        if (gender != Gender.Unknown)
            return gender;

        if (sd.FileSpeakerMap.TryGetValue(strRef, out var fileName))
        {
            gender = sd.FileGenderMap.Get(fileName);
            if (gender != Gender.Unknown)
                return gender;
        }

        if (sd.LiveCreLookup is not null && sd.LiveSpeakerMap.TryGetValue(strRef, out var liveName))
            gender = sd.LiveCreLookup.Resolve(liveName);

        return gender;
    }

    private static string? ResolveDisplayName(int? nameStrRef, TlkFile tlk, DialogTextCleaner cleaner)
    {
        if (nameStrRef is not { } strRef || strRef < 0 || strRef >= tlk.Entries.Count)
            return null;

        var cleaned = cleaner.Clean(tlk.Entries[strRef].Text);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static List<(TlkEntry Entry, string CleanedText)> GetTtsCandidates(
        TlkFile tlk, DialogTextCleaner cleaner, int minLength, int limit,
        ISet<int>? requireKnownSpeaker = null, bool parseAll = false,
        IReadOnlyDictionary<int, string>? textOverrides = null)
    {
        var candidates = new List<(TlkEntry Entry, string CleanedText)>();
        foreach (var entry in tlk.Entries)
        {
            if (!entry.HasText) continue;
            if (!parseAll && entry.HasSound) continue;
            if (requireKnownSpeaker is not null && !requireKnownSpeaker.Contains(entry.StrRef)) continue;

            // A hand-edited override always wins over re-deriving the text from
            // scratch - the user already finalized the exact wording for this line.
            var cleaned = textOverrides is not null && textOverrides.TryGetValue(entry.StrRef, out var overrideText)
                ? overrideText
                : cleaner.Clean(entry.Text);

            if (cleaner.LooksSpeakable(cleaned, minLength))
                candidates.Add((entry, cleaned));

            if (candidates.Count >= limit) break;
        }
        return candidates;
    }

    private static void EnsureBackup(string tlkPath)
    {
        var backupPath = tlkPath + ".bak";
        if (!File.Exists(backupPath))
            File.Copy(tlkPath, backupPath);
    }

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static PipelineResult Fail(IProgress<string> log, string message)
    {
        log.Report($"ERROR: {message}");
        return new PipelineResult(false, 0, 0, 0, message);
    }

    public async Task RepatchAsync(PipelineOptions options, IProgress<string> log, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(options.TlkPath))
                throw new FileNotFoundException($"dialog.tlk not found: '{options.TlkPath}'");
            if (!File.Exists(options.ManifestPath))
                throw new FileNotFoundException($"Manifest not found: '{options.ManifestPath}'");

            var encoding = options.Encoding;
            var manifest = VoiceManifest.LoadOrCreate(options.ManifestPath);
            var tlk = TlkFile.Load(options.TlkPath, encoding);

            var toRepatch = manifest.Entries
                .Where(e => e.Status == VoiceEntryStatus.Patched)
                .OrderBy(e => e.StrRef)
                .ToList();

            log.Report($"Manifest entries: {toRepatch.Count}");

            if (toRepatch.Count == 0)
            {
                log.Report("Nothing to repatch.");
                return;
            }

            var backupPath = options.TlkPath + ".bak";
            if (!File.Exists(backupPath))
                File.Copy(options.TlkPath, backupPath);
            log.Report($"Backup: {backupPath}");

            using var patcher = TlkPatcher.Open(options.TlkPath);
            var repatched = 0;
            var skipped = 0;

            foreach (var me in toRepatch)
            {
                ct.ThrowIfCancellationRequested();

                if (me.StrRef >= tlk.Entries.Count) { skipped++; continue; }

                var te = tlk.Entries[me.StrRef];
                if (!string.Equals(te.SoundResRef, me.ResRef, StringComparison.OrdinalIgnoreCase))
                {
                    patcher.ApplySound(te, me.ResRef);
                    repatched++;
                }
            }

            log.Report($"Done. Repatched: {repatched}, already correct: {toRepatch.Count - repatched - skipped}, skipped: {skipped}.");
        }, ct);
    }

    public async Task UnpatchAsync(PipelineOptions options, IProgress<string> log, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(options.TlkPath))
                throw new FileNotFoundException($"dialog.tlk not found: '{options.TlkPath}'");
            if (!File.Exists(options.ManifestPath))
                throw new FileNotFoundException($"Manifest not found: '{options.ManifestPath}'");

            var encoding = options.Encoding;
            var manifest = VoiceManifest.LoadOrCreate(options.ManifestPath);
            var tlk = TlkFile.Load(options.TlkPath, encoding);

            var toUnpatch = manifest.Entries
                .Where(e => e.Status == VoiceEntryStatus.Patched)
                .OrderBy(e => e.StrRef)
                .ToList();

            log.Report($"Manifest entries: {toUnpatch.Count}");

            if (toUnpatch.Count == 0)
            {
                log.Report("Nothing to unpatch.");
                return;
            }

            var backupPath = options.TlkPath + ".bak";
            if (!File.Exists(backupPath))
                File.Copy(options.TlkPath, backupPath);
            log.Report($"Backup: {backupPath}");

            using var patcher = TlkPatcher.Open(options.TlkPath);
            var cleared = 0;
            var skipped = 0;

            foreach (var me in toUnpatch)
            {
                ct.ThrowIfCancellationRequested();

                if (me.StrRef >= tlk.Entries.Count) { skipped++; continue; }

                var te = tlk.Entries[me.StrRef];
                if (!string.Equals(te.SoundResRef, me.ResRef, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                patcher.ClearSound(te);
                cleared++;
            }

            log.Report($"Done. Cleared: {cleared}, skipped: {skipped}.");
            log.Report("Note: .wav/.ogg files in override were NOT deleted.");
        }, ct);
    }

    private static List<(TlkEntry Entry, string CleanedText)> GetCandidatesForStrRefs(
    TlkFile tlk, DialogTextCleaner cleaner, int minLength, IReadOnlySet<int> strRefs,
    IReadOnlyDictionary<int, string>? textOverrides, IProgress<string> log)
    {
        var candidates = new List<(TlkEntry Entry, string CleanedText)>();

        foreach (var strRef in strRefs.OrderBy(s => s))
        {
            if (strRef < 0 || strRef >= tlk.Entries.Count)
            {
                log.Report($"  [{strRef}] skipped: out of range for this TLK ({tlk.Entries.Count} entries).");
                continue;
            }

            var entry = tlk.Entries[strRef];
            if (!entry.HasText)
            {
                log.Report($"  [{strRef}] skipped: no text.");
                continue;
            }

            var cleaned = textOverrides is not null && textOverrides.TryGetValue(strRef, out var overrideText)
                ? overrideText
                : cleaner.Clean(entry.Text);

            if (!cleaner.LooksSpeakable(cleaned, minLength))
            {
                log.Report($"  [{strRef}] skipped: not speakable after cleaning.");
                continue;
            }

            if (entry.HasSound)
                log.Report($"  [{strRef}] already has sound ('{entry.SoundResRef}') - regenerating anyway since it was explicitly selected.");

            candidates.Add((entry, cleaned));
        }

        return candidates;
    }
}
