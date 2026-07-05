using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgTtsVoicePatcher.Audio;
using BgTtsVoicePatcher.Config;
using BgTtsVoicePatcher.Cre;
using BgTtsVoicePatcher.ResRef;
using BgTtsVoicePatcher.Reporting;
using BgTtsVoicePatcher.Speakers;
using BgTtsVoicePatcher.Speech;
using BgTtsVoicePatcher.State;
using BgTtsVoicePatcher.Text;
using BgTtsVoicePatcher.Tlk;

namespace BgTtsVoicePatcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args, 1);

        try
        {
            return command switch
            {
                "voices" => RunVoices(),
                "scan" => RunScan(options),
                "speakers" => RunSpeakers(options),
                "report" => RunReport(options),
                "generate" => RunGenerate(options),
                "run" => RunAll(options),
                "help" or "--help" or "-h" => Help(),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args, int startIndex)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = startIndex; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[key] = hasValue ? args[++i] : "true";
        }
        return options;
    }

    // ---- commands ----------------------------------------------------------

    private static int RunVoices()
    {
        Console.WriteLine("Installed SAPI voices:");
        foreach (var voice in VoiceSynthesizer.ListInstalledVoices())
            Console.WriteLine($"  {voice}");
        return 0;
    }

    private static int RunScan(IReadOnlyDictionary<string, string> options)
    {
        var tlkPath = RequireOption(options, "tlk");
        var encoding = ResolveEncoding(options.GetValueOrDefault("encoding", "windows-1252"));
        var minLength = int.Parse(options.GetValueOrDefault("min-length", "2"), CultureInfo.InvariantCulture);

        var tlk = TlkFile.Load(tlkPath, encoding);
        var cleaner = BuildCleaner(LoadConfig(options));

        var withText = 0;
        var withSound = 0;
        var candidates = new List<(int StrRef, string Text)>();

        foreach (var entry in tlk.Entries)
        {
            if (!entry.HasText) continue;
            withText++;

            if (entry.HasSound)
            {
                withSound++;
                continue;
            }

            var cleaned = cleaner.Clean(entry.Text);
            if (cleaner.LooksSpeakable(cleaned, minLength))
                candidates.Add((entry.StrRef, cleaned));
        }

        Console.WriteLine($"TLK file: {tlkPath}");
        Console.WriteLine($"  Total entries:     {tlk.Entries.Count}");
        Console.WriteLine($"  Entries with text: {withText}");
        Console.WriteLine($"  Already voiced:    {withSound}");
        Console.WriteLine($"  TTS candidates:    {candidates.Count}");
        Console.WriteLine();
        Console.WriteLine("Sample candidates:");
        foreach (var (strRef, text) in candidates.Take(8))
            Console.WriteLine($"  [{strRef}] {Truncate(text, 90)}");

        return 0;
    }

    private static int RunSpeakers(IReadOnlyDictionary<string, string> options)
    {
        var tlkPath = RequireOption(options, "tlk");
        var dlgDir = RequireOption(options, "dlg-dir");
        var creDir = options.GetValueOrDefault("cre-dir");
        var encoding = ResolveEncoding(options.GetValueOrDefault("encoding", "windows-1252"));
        var minLength = int.Parse(options.GetValueOrDefault("min-length", "2"), CultureInfo.InvariantCulture);
        var outMap = options.GetValueOrDefault("out-map", System.IO.Path.Combine(dlgDir, "speaker-strrefs.json"));
        var outNames = options.GetValueOrDefault("out-names", System.IO.Path.Combine(dlgDir, "speaker-names.json"));
        var outStats = options.GetValueOrDefault("out-stats", System.IO.Path.Combine(dlgDir, "speaker-stats.json"));
        var outUnmatched = options.GetValueOrDefault("out-unmatched", System.IO.Path.Combine(dlgDir, "speaker-unmatched.txt"));

        var config = LoadConfig(options);
        var tlk = TlkFile.Load(tlkPath, encoding);
        var cleaner = BuildCleaner(config);
        var candidates = GetTtsCandidates(tlk, cleaner, minLength, int.MaxValue, parseAll: true);
        var candidateStrRefs = candidates.Select(c => c.Entry.StrRef).ToHashSet();

        Console.WriteLine($"Scanning *.dlg in: {dlgDir}");
        var scan = SpeakerIndex.Scan(dlgDir);
        Console.WriteLine($"  Files scanned: {scan.FilesScanned}, failed to parse: {scan.FilesFailed}");

        var relevantMap = scan.StrRefToSpeaker
            .Where(kv => candidateStrRefs.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var relevantNames = relevantMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        CreGenderLookup? creLookup = string.IsNullOrWhiteSpace(creDir) ? null : new CreGenderLookup(creDir, config);
        var resolvedViaCre = 0;

        var speakerInfos = new List<SpeakerIndex.SpeakerInfo>();
        foreach (var systemName in relevantNames)
        {
            var info = creLookup?.ResolveInfo(systemName);
            var gender = info?.Gender ?? Gender.Unknown;
            var displayName = ResolveDisplayName(info?.NameStrRef, tlk, cleaner);

            if (gender != Gender.Unknown)
                resolvedViaCre++;

            speakerInfos.Add(new SpeakerIndex.SpeakerInfo(systemName, displayName, gender));
        }

        SpeakerIndex.SaveStrRefMap(relevantMap, outMap);
        SpeakerIndex.SaveOrMergeNamesFile(speakerInfos, outNames);
        var unresolvedLinesCount = SpeakerIndex.SaveOrMergeStatsFile(relevantMap, speakerInfos, outStats);

        var unmatched = candidates
            .Where(c => !relevantMap.ContainsKey(c.Entry.StrRef))
            .OrderBy(c => c.Entry.StrRef)
            .ToList();

        File.WriteAllLines(outUnmatched, unmatched.Select(c => $"[{c.Entry.StrRef}] {c.CleanedText}"));

        Console.WriteLine($"  TTS candidates with a known speaker: {relevantMap.Count} / {candidateStrRefs.Count}");
        Console.WriteLine($"  Distinct speaker names:    {relevantNames.Count}");
        if (creLookup is not null)
        {
            Console.WriteLine($"  Gendered via CRE Sex byte: {resolvedViaCre} / {relevantNames.Count}");
            Console.WriteLine($"  Total dialogue lines with unresolved gender: {unresolvedLinesCount}");
        }
        Console.WriteLine($"  No speaker found:          {unmatched.Count} -> {outUnmatched}");
        Console.WriteLine("    (often item/spell descriptions, journal text, etc. - not every line is");
        Console.WriteLine("     spoken dialogue, so this list isn't necessarily a gap. Worth a skim though.)");
        Console.WriteLine($"  Wrote: {outMap}");
        Console.WriteLine($"  Wrote/updated: {outNames}");
        Console.WriteLine($"  Wrote stats: {outStats}");
        Console.WriteLine();
        Console.WriteLine($"Open {outNames} to fill in any remaining nulls or correct a wrong CRE-based guess,");
        Console.WriteLine("then pass both files to 'generate' via --speaker-map and --name-gender-map.");

        return 0;
    }

    /// <summary>Looks up a CRE's name StrRef in the already-loaded TLK to get the
    /// actual in-game display name (e.g. "Jaheira"), cleaned the same way dialogue
    /// text is. Null if there's no name StrRef or it doesn't resolve to real text.</summary>
    private static string? ResolveDisplayName(int? nameStrRef, TlkFile tlk, DialogTextCleaner cleaner)
    {
        if (nameStrRef is not { } strRef || strRef < 0 || strRef >= tlk.Entries.Count)
            return null;

        var cleaned = cleaner.Clean(tlk.Entries[strRef].Text);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static int RunReport(IReadOnlyDictionary<string, string> options)
    {
        var tlkPath = RequireOption(options, "tlk");
        var encoding = ResolveEncoding(options.GetValueOrDefault("encoding", "windows-1252"));
        var overrideDir = options.GetValueOrDefault("override");

        var outPath = options.GetValueOrDefault(
            "out",
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(tlkPath))!, "dialog-report.csv"));

        var format = options.GetValueOrDefault("format")?.ToLowerInvariant()
                     ?? (System.IO.Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant() is "json" ? "json" : "csv");

        var tlk = TlkFile.Load(tlkPath, encoding);
        var config = LoadConfig(options);
        var cleaner = BuildCleaner(config);

        var dlgDir = options.GetValueOrDefault("dlg-dir") ?? string.Empty;
        var sd = ResolveSpeakerData(options, dlgDir, config);

        var rows = new List<DialogReportRow>();

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

            if (realName is not null && systemName is not null && realName.Equals(systemName, StringComparison.OrdinalIgnoreCase))
                realName = null;

            var soundResRef = entry.HasSound ? entry.SoundResRef : null;
            bool? soundFileExists = null;
            if (!string.IsNullOrWhiteSpace(overrideDir) && soundResRef is not null)
                soundFileExists = File.Exists(System.IO.Path.Combine(overrideDir, soundResRef + ".wav"));

            rows.Add(new DialogReportRow(
                entry.StrRef,
                systemName,
                realName,
                gender switch { Gender.Male => "M", Gender.Female => "F", _ => "" },
                entry.HasSound,
                soundResRef,
                soundFileExists,
                entry.Text));
        }

        if (format == "json")
            DialogReportWriter.WriteJson(rows, outPath);
        else
            DialogReportWriter.WriteCsv(rows, outPath);

        Console.WriteLine($"Wrote {rows.Count} row(s) to {outPath} ({format}).");
        return 0;
    }

    private static List<(TlkEntry Entry, string CleanedText)> GetTtsCandidates(
        TlkFile tlk, DialogTextCleaner cleaner, int minLength, int limit, ISet<int>? requireKnownSpeaker = null, bool parseAll = false)
    {
        var candidates = new List<(TlkEntry Entry, string CleanedText)>();
        foreach (var entry in tlk.Entries)
        {
            if (!entry.HasText) continue;
            if (!parseAll && entry.HasSound) continue;
            if (requireKnownSpeaker is not null && !requireKnownSpeaker.Contains(entry.StrRef)) continue;

            var cleaned = cleaner.Clean(entry.Text);
            if (cleaner.LooksSpeakable(cleaned, minLength))
                candidates.Add((entry, cleaned));

            if (candidates.Count >= limit) break;
        }
        return candidates;
    }

    /// <summary>Builds candidates from an explicit StrRef list instead of scanning the
    /// whole TLK for unvoiced lines. Unlike the normal path, this deliberately does
    /// NOT skip entries that already have sound - an explicit list is a request to
    /// (re)generate exactly those lines, regardless of their current voiced state.</summary>
    private static List<(TlkEntry Entry, string CleanedText)> GetCandidatesForStrRefs(
        TlkFile tlk, DialogTextCleaner cleaner, int minLength, IReadOnlySet<int> strRefs)
    {
        var candidates = new List<(TlkEntry Entry, string CleanedText)>();

        foreach (var strRef in strRefs.OrderBy(s => s))
        {
            if (strRef < 0 || strRef >= tlk.Entries.Count)
            {
                Console.Error.WriteLine($"  [{strRef}] skipped: out of range for this TLK ({tlk.Entries.Count} entries).");
                continue;
            }

            var entry = tlk.Entries[strRef];
            if (!entry.HasText)
            {
                Console.Error.WriteLine($"  [{strRef}] skipped: no text.");
                continue;
            }

            var cleaned = cleaner.Clean(entry.Text);
            if (!cleaner.LooksSpeakable(cleaned, minLength))
            {
                Console.Error.WriteLine($"  [{strRef}] skipped: not speakable after cleaning ('{Truncate(cleaned, 40)}').");
                continue;
            }

            if (entry.HasSound)
                Console.WriteLine($"  [{strRef}] already has sound ('{entry.SoundResRef}') - regenerating anyway since it was explicitly listed.");

            candidates.Add((entry, cleaned));
        }

        return candidates;
    }

    /// <summary>Loads a flat JSON array of StrRef numbers, e.g. [12345, 67890], for
    /// --strrefs. Null if no path was given (meaning: no filter, use normal selection).</summary>
    private static HashSet<int>? LoadStrRefFilter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
            throw new FileNotFoundException($"StrRef list not found at '{path}'.");

        var list = JsonSerializer.Deserialize<List<int>>(File.ReadAllText(path)) ?? new List<int>();
        return list.ToHashSet();
    }

    private static int RunGenerate(IReadOnlyDictionary<string, string> options)
    {
        var tlkPath = RequireOption(options, "tlk");
        var overrideDir = RequireOption(options, "override");

        if (!File.Exists(tlkPath))
            throw new FileNotFoundException($"dialog.tlk not found at '{tlkPath}'.");

        var config = LoadConfig(options);

        var manifestPath = options.GetValueOrDefault(
            "manifest",
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(tlkPath))!, "tts-manifest.json"));

        var voiceName = options.GetValueOrDefault("voice") ?? options.GetValueOrDefault("default-voice");
        var maleVoice = options.GetValueOrDefault("male-voice");
        var femaleVoice = options.GetValueOrDefault("female-voice");
        var genderMap = GenderMap.Load(options.GetValueOrDefault("gender-map"));
        var dlgDir = options.GetValueOrDefault("dlg-dir", overrideDir);
        var sd = ResolveSpeakerData(options, dlgDir, config);

        var rate = int.Parse(options.GetValueOrDefault("rate", "0"), CultureInfo.InvariantCulture);
        var volume = int.Parse(options.GetValueOrDefault("volume", "100"), CultureInfo.InvariantCulture);
        var prefix = options.GetValueOrDefault("prefix", "TS").ToUpperInvariant();
        var encoding = ResolveEncoding(options.GetValueOrDefault("encoding", "windows-1252"));
        var minLength = int.Parse(options.GetValueOrDefault("min-length", "2"), CultureInfo.InvariantCulture);
        var limit = int.Parse(options.GetValueOrDefault("limit", int.MaxValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var dryRun = options.ContainsKey("dry-run");

        Directory.CreateDirectory(overrideDir);

        var tlk = TlkFile.Load(tlkPath, encoding);
        var cleaner = BuildCleaner(config);

        var strRefFilter = LoadStrRefFilter(options.GetValueOrDefault("strrefs"));

        var candidates = strRefFilter is not null
            ? GetCandidatesForStrRefs(tlk, cleaner, minLength, strRefFilter)
            : GetTtsCandidates(tlk, cleaner, minLength, limit, requireKnownSpeaker: sd.KnownSpeakers);

        Console.WriteLine(strRefFilter is not null
            ? $"{candidates.Count} line(s) selected from --strrefs ({strRefFilter.Count} requested)."
            : $"{candidates.Count} unvoiced line(s) selected (limit={limit}).");

        using var synth = new VoiceSynthesizer(voiceName, maleVoice, femaleVoice, rate, volume);

        var useOgg = options.ContainsKey("ogg");
        var ffmpegPath = options.GetValueOrDefault("ffmpeg", "ffmpeg");
        var oggQuality = int.Parse(options.GetValueOrDefault("ogg-quality", "2"), CultureInfo.InvariantCulture);
        var oggEncoder = useOgg ? new OggEncoder(ffmpegPath, oggQuality) : null;

        VoiceManifest? manifest = null;

        if (!dryRun)
        {
            var backupPath = EnsureBackup(tlkPath);
            Console.WriteLine($"Backup of original TLK at: {backupPath}");
            manifest = VoiceManifest.LoadOrCreate(manifestPath);
        }
        else
        {
            Console.WriteLine($"Dry run - writing WAV files to {overrideDir} only.");
            Console.WriteLine($"{tlkPath} will NOT be touched and no manifest will be written.");
        }

        using var patcher = dryRun ? null : TlkPatcher.Open(tlkPath);

        Console.WriteLine();

        var generated = 0;
        var reused = 0;
        var failed = 0;
        var progressTimer = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < candidates.Count; i++)
        {
            var (entry, cleaned) = candidates[i];
            var resRef = ResRefAllocator.ForStrRef(entry.StrRef, prefix);
            var wavPath = System.IO.Path.Combine(overrideDir, resRef + ".wav");
            var gender = ResolveGender(entry.StrRef, genderMap, sd);
            var voiceUsed = synth.VoiceNameFor(gender);
            var textHash = ComputeHash(cleaned + "|" + voiceUsed + "|" + (useOgg ? "ogg" : "pcm"));

            try
            {
                // Dry runs and explicit --strrefs both bypass the reuse check on
                // purpose - dry runs because they never record anything to reuse
                // from, --strrefs because it means "regenerate this, period".
                var existing = !dryRun && strRefFilter is null ? manifest!.Get(entry.StrRef) : null;
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

                if (!dryRun)
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
                if (!dryRun)
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
                Console.Error.WriteLine($"  [{entry.StrRef}] FAILED: {ex.Message}");
            }

            // Fires on the very first item (so it doesn't look stuck right away),
            // at least once a second after that, and always on the last item -
            // regardless of batch size, so a --limit 10 dry run gets feedback too.
            if (i == 0 || i == candidates.Count - 1 || progressTimer.ElapsedMilliseconds >= 1000)
            {
                manifest?.Save();
                Console.WriteLine($"  progress: {i + 1}/{candidates.Count} (generated {generated}, reused {reused}, failed {failed})");
                progressTimer.Restart();
            }
        }

        if (dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("Sample:");
            foreach (var (entry, cleaned) in candidates.Take(8))
            {
                var resRef = ResRefAllocator.ForStrRef(entry.StrRef, prefix);
                var voiceUsed = synth.VoiceNameFor(ResolveGender(entry.StrRef, genderMap, sd));
                Console.WriteLine($"  [{entry.StrRef}] -> {resRef}.wav ({voiceUsed}) : {Truncate(cleaned, 80)}");
            }

            Console.WriteLine();
            Console.WriteLine($"Wrote {generated} WAV file(s) ({failed} failed). Listen, then re-run without --dry-run");
            Console.WriteLine("to patch dialog.tlk. Since this run never touched the manifest, that next run has");
            Console.WriteLine("no record of these files and will resynthesize them from scratch (same text/voice");
            Console.WriteLine("in, so the audio should come out the same - just a few extra seconds).");

            return failed > 0 ? 2 : 0;
        }

        Console.WriteLine();
        Console.WriteLine("Done.");
        Console.WriteLine($"  Generated audio: {generated}");
        Console.WriteLine($"  Reused audio:    {reused}");
        Console.WriteLine($"  Failed:          {failed}");
        Console.WriteLine($"  Patched into:    {tlkPath}");
        Console.WriteLine($"  Manifest:        {manifestPath}");

        return failed > 0 ? 2 : 0;
    }

    // ---- helpers ------------------------------------------------------------

    private static PatcherConfig LoadConfig(IReadOnlyDictionary<string, string> options) =>
        PatcherConfig.Load(options.GetValueOrDefault("config"));

    private static DialogTextCleaner BuildCleaner(PatcherConfig config) => new(config);

    private static int RunAll(IReadOnlyDictionary<string, string> options)
    {
        var config = LoadConfig(options);
        var gameDir = RequireOption(options, "game-dir");
        var lang = options.GetValueOrDefault("lang", "en_US");

        var overrideDir = System.IO.Path.Combine(gameDir, "override");
        var langDir = System.IO.Path.Combine(gameDir, "lang", lang);
        var tlkPath = System.IO.Path.Combine(langDir, "dialog.tlk");
        var speakerMapPath = System.IO.Path.Combine(langDir, "speaker-strrefs.json");
        var speakerNamesPath = System.IO.Path.Combine(langDir, "speaker-names.json");

        if (!Directory.Exists(overrideDir))
            throw new DirectoryNotFoundException($"override directory not found: '{overrideDir}'");
        if (!File.Exists(tlkPath))
            throw new FileNotFoundException($"dialog.tlk not found: '{tlkPath}'. Is --lang '{lang}' correct?");

        var dlgDir = options.GetValueOrDefault("dlg-dir", overrideDir);
        var creDir = options.GetValueOrDefault("cre-dir");

        Console.WriteLine($"Game directory : {gameDir}");
        Console.WriteLine($"Override       : {overrideDir}");
        Console.WriteLine($"TLK            : {tlkPath}");
        Console.WriteLine($"DLG scan dir   : {dlgDir}");
        if (!string.IsNullOrWhiteSpace(creDir))
            Console.WriteLine($"CRE dir        : {creDir}");
        Console.WriteLine();

        // -- Step 1: voice selection ----------------------------------------
        var allVoices = VoiceSynthesizer.ListInstalledVoices().ToList();
        if (allVoices.Count == 0)
            throw new InvalidOperationException("No SAPI voices installed. Install at least one voice before running.");

        var voiceName   = ResolveVoiceOption(options, "voice",        allVoices, "default (fallback for unresolved gender)");
        var maleVoice   = ResolveVoiceOption(options, "male-voice",   allVoices, "male");
        var femaleVoice = ResolveVoiceOption(options, "female-voice", allVoices, "female");

        if (voiceName is null && maleVoice is null && femaleVoice is null)
        {
            Console.Error.WriteLine("No voices selected. At least one voice is required.");
            return 1;
        }
        Console.WriteLine();

        // -- Step 2: speakers -----------------------------------------------
        if (!File.Exists(speakerMapPath) || !File.Exists(speakerNamesPath))
        {
            Console.WriteLine("Speaker files not found - running 'speakers' to generate them...");
            Console.WriteLine();
            var speakersOpts = BuildOptions(options,
                ("tlk",          tlkPath),
                ("dlg-dir",      dlgDir),
                ("out-map",      speakerMapPath),
                ("out-names",    speakerNamesPath),
                ("out-unmatched",System.IO.Path.Combine(langDir, "speaker-unmatched.txt")));
            if (!string.IsNullOrWhiteSpace(creDir))
                speakersOpts["cre-dir"] = creDir;
            var speakersResult = RunSpeakers(speakersOpts);
            if (speakersResult != 0) return speakersResult;
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"Speaker files found in {langDir} - skipping 'speakers'.");
            Console.WriteLine();
        }

        // -- Step 3: generate -----------------------------------------------
        Console.WriteLine("Running 'generate'...");
        Console.WriteLine();
        var generateOpts = BuildOptions(options,
            ("tlk",           tlkPath),
            ("override",      overrideDir),
            ("speaker-map",   speakerMapPath),
            ("name-gender-map", speakerNamesPath),
            ("dlg-dir",       dlgDir));
        if (!string.IsNullOrWhiteSpace(creDir))      generateOpts["cre-dir"]      = creDir;
        if (!string.IsNullOrWhiteSpace(voiceName))   generateOpts["voice"]        = voiceName;
        if (!string.IsNullOrWhiteSpace(maleVoice))   generateOpts["male-voice"]   = maleVoice;
        if (!string.IsNullOrWhiteSpace(femaleVoice)) generateOpts["female-voice"] = femaleVoice;
        var generateResult = RunGenerate(generateOpts);

        // -- Step 4: report -------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("Running 'report'...");
        var reportOpts = BuildOptions(options,
            ("tlk",           tlkPath),
            ("override",      overrideDir),
            ("speaker-map",   speakerMapPath),
            ("name-gender-map", speakerNamesPath),
            ("out",           System.IO.Path.Combine(langDir, "dialog-report.csv")));
        RunReport(reportOpts);

        return generateResult;
    }

    /// <summary>Returns the voice name from options if already provided and non-empty;
    /// otherwise shows the installed voice list and prompts the user to pick one.
    /// Returns null if the user presses Enter (skip / no voice for this slot).</summary>
    private static string? ResolveVoiceOption(
        IReadOnlyDictionary<string, string> options, string key,
        IReadOnlyList<string> voices, string label)
    {
        if (options.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        Console.WriteLine($"Select {label} voice (number 1-{voices.Count}, or exact name; Enter to skip):");
        for (var i = 0; i < voices.Count; i++)
            Console.WriteLine($"  {i + 1}. {voices[i]}");
        Console.Write("> ");
        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input))
            return null;

        if (int.TryParse(input, out var idx) && idx >= 1 && idx <= voices.Count)
        {
            // Strip the "  (culture, gender, age)" suffix to get just the SAPI name.
            var full = voices[idx - 1];
            var sep = full.IndexOf("  (", StringComparison.Ordinal);
            return sep > 0 ? full[..sep].Trim() : full.Trim();
        }

        return input; // treat as a literal SAPI voice name
    }

    /// <summary>Clones the base options dictionary and merges in the given overrides,
    /// without mutating the original. Used to build per-sub-command option sets in RunAll.</summary>
    private static Dictionary<string, string> BuildOptions(
        IReadOnlyDictionary<string, string> baseOptions,
        params (string Key, string Value)[] overrides)
    {
        var result = new Dictionary<string, string>(baseOptions, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrides)
            result[key] = value;
        return result;
    }

    /// <summary>All speaker-resolution data for a single command invocation, pre-built
    /// so RunGenerate and RunReport don't duplicate the loading + scanning logic.</summary>
    private sealed record SpeakerData(
        Dictionary<int, string> FileSpeakerMap,
        SpeakerGenderMap FileGenderMap,
        Dictionary<int, string> LiveSpeakerMap,
        CreGenderLookup? LiveCreLookup,
        HashSet<int> KnownSpeakers);

    /// <summary>
    /// Loads file-based speaker data (--speaker-map / --name-gender-map) and, only
    /// when actually needed, scans DLGs and/or creates a CRE lookup:
    ///
    ///   • DLG scan is SKIPPED when --speaker-map is present and non-empty AND
    ///     --cre-dir is not provided - the file data is sufficient for both the
    ///     unmatched filter and gender resolution.
    ///   • DLG scan runs if --speaker-map is empty (need live data for the filter)
    ///     OR if --cre-dir is given (need liveSpeakerMap to drive the CRE lookup).
    ///   • CreGenderLookup is only built when --cre-dir is provided.
    /// </summary>
    private static SpeakerData ResolveSpeakerData(
        IReadOnlyDictionary<string, string> options,
        string dlgDir,
        PatcherConfig config)
    {
        var fileSpeakerMap = SpeakerIndex.LoadStrRefMap(options.GetValueOrDefault("speaker-map") ?? string.Empty);
        var fileGenderMap  = SpeakerGenderMap.Load(options.GetValueOrDefault("name-gender-map"));
        var creDir         = options.GetValueOrDefault("cre-dir");

        var hasSpeakerFile = fileSpeakerMap.Count > 0;
        var needCreLookup  = !string.IsNullOrWhiteSpace(creDir);
        var needDlgScan    = !hasSpeakerFile || needCreLookup;

        var liveSpeakerMap = new Dictionary<int, string>();
        CreGenderLookup? liveCreLookup = null;

        if (needDlgScan)
        {
            var reason = !hasSpeakerFile ? "no --speaker-map provided" : "--cre-dir requested";
            Console.WriteLine($"Scanning *.dlg in: {dlgDir} ({reason})");
            var dlgScan = SpeakerIndex.Scan(dlgDir);
            Console.WriteLine($"  Files scanned: {dlgScan.FilesScanned}, failed: {dlgScan.FilesFailed}");
            liveSpeakerMap = dlgScan.StrRefToSpeaker;
        }
        else
        {
            Console.WriteLine($"Using --speaker-map ({fileSpeakerMap.Count} entries) — DLG scan skipped.");
        }

        if (needCreLookup)
            liveCreLookup = new CreGenderLookup(creDir!, config);

        var knownSpeakers = new HashSet<int>(fileSpeakerMap.Keys.Concat(liveSpeakerMap.Keys));
        if (knownSpeakers.Count == 0)
            Console.WriteLine("  Warning: no speaker data found. No lines will be selected.");

        Console.WriteLine();
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

    private static string RequireOption(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value)
            ? value
            : throw new ArgumentException($"Missing required option --{key}");

    private static Encoding ResolveEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return name.Trim().ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => Encoding.UTF8,
            "ascii" => Encoding.ASCII,
            "windows-1252" or "1252" => Encoding.GetEncoding(1252),
            "windows-1250" or "1250" => Encoding.GetEncoding(1250), // common for Czech fan translations
            _ => Encoding.GetEncoding(name)
        };
    }

    private static string EnsureBackup(string tlkPath)
    {
        var backupPath = tlkPath + ".bak";
        if (!File.Exists(backupPath))
            File.Copy(tlkPath, backupPath);
        return backupPath;
    }

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "..." : text;

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return 1;
    }

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("BgTtsVoicePatcher - pre-bake SAPI text-to-speech for unvoiced Infinity Engine dialog.tlk lines");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine();
        Console.WriteLine("  run --game-dir <path> [--lang <code>] [--voice <n>] [--male-voice <n>] [--female-voice <n>]");
        Console.WriteLine("       [--dlg-dir <dir>] [--cre-dir <dir>] [--ogg] [--ffmpeg <path>] [--ogg-quality 0..10]");
        Console.WriteLine("       [--rate -10..10] [--volume 0..100] [--prefix <2 chars>] [--limit <n>] [--dry-run]");
        Console.WriteLine("       [--config <path>]");
        Console.WriteLine("      Full pipeline from a single game directory. Determines override/, lang/<code>/dialog.tlk");
        Console.WriteLine("      automatically (--lang defaults to en_US). If speaker-strrefs.json and speaker-names.json");
        Console.WriteLine("      are absent from the lang directory, runs 'speakers' first to generate them, then runs");
        Console.WriteLine("      'generate', then 'report'. Prompts interactively for any voice not given on the command line.");
        Console.WriteLine();
        Console.WriteLine("  voices");
        Console.WriteLine("      List installed SAPI voices (NaturalVoiceSAPIAdapter voices show up here too).");
        Console.WriteLine();
        Console.WriteLine("  scan --tlk <path> [--encoding <name>] [--min-length <n>] [--config <path>]");
        Console.WriteLine("      Report how many lines are already voiced vs. TTS candidates. Writes nothing.");
        Console.WriteLine();
        Console.WriteLine("  speakers --tlk <path> --dlg-dir <dir> [--cre-dir <dir>]");
        Console.WriteLine("            [--out-map <path>] [--out-names <path>] [--out-stats <path>] [--out-unmatched <path>]");
        Console.WriteLine("      Scans every *.dlg in --dlg-dir for NPC lines and writes:");
        Console.WriteLine("        speaker-strrefs.json   (auto-generated StrRef -> speaker name, don't edit)");
        Console.WriteLine("        speaker-names.json     (speaker names grouped by in-game display name, e.g.");
        Console.WriteLine("                                 { \"Jaheira\": { \"JAHEIRA\": \"F\", \"BJAHEIR\": \"F\" } } -");
        Console.WriteLine("                                 pre-filled from each speaker's CRE if --cre-dir is given");
        Console.WriteLine("                                 (display name from the CRE's own name StrRef, gender from");
        Console.WriteLine("                                 its Sex byte); open it to fill in any null or fix a wrong");
        Console.WriteLine("                                 guess. Reruns only add brand-new names, never touching");
        Console.WriteLine("                                 ones already in the file.");
        Console.WriteLine("        speaker-stats.json     (per-display-name and per-system-name line counts, plus");
        Console.WriteLine("                                 a summary of how many TTS candidates had no speaker found.");
        Console.WriteLine("                                 Useful for spotting which characters have the most unvoiced");
        Console.WriteLine("                                 lines and for reviewing coverage after a generate run.)");
        Console.WriteLine("        speaker-unmatched.txt  (every TTS candidate with no speaker found at all, for");
        Console.WriteLine("                                 review - usually non-dialogue text, not a real gap)");
        Console.WriteLine();
        Console.WriteLine("  report --tlk <path> [--override <dir>]");
        Console.WriteLine("          [--dlg-dir <dir>] [--cre-dir <dir>] [--speaker-map <path>] [--name-gender-map <path>]");
        Console.WriteLine("          [--out <path>] [--format csv|json] [--encoding <name>]");
        Console.WriteLine("      Read-only dump of every dialog.tlk entry that has text: StrRef, speaker system");
        Console.WriteLine("      name, resolved real name, gender, whether it has sound, which SoundResRef, and");
        Console.WriteLine("      the raw text. Names/gender come from --speaker-map/--name-gender-map (the files");
        Console.WriteLine("      from 'speakers') and/or a live --dlg-dir/--cre-dir scan, same as 'generate' -");
        Console.WriteLine("      give either, both, or neither (StrRef/sound/text still show either way). Pass");
        Console.WriteLine("      --override too to add a SoundFileExists column verifying the .wav is actually");
        Console.WriteLine("      on disk - not required, since HasSound/SoundResRef both already come straight");
        Console.WriteLine("      from dialog.tlk itself. Defaults to CSV next to the TLK; no synthesis happens.");
        Console.WriteLine();
        Console.WriteLine("  generate --tlk <path> --override <dir>");
        Console.WriteLine("            [--manifest <path>] [--voice <name>] [--default-voice <name>]");
        Console.WriteLine("            [--male-voice <name>] [--female-voice <name>]");
        Console.WriteLine("            [--gender-map <path.json>] [--speaker-map <path.json>] [--name-gender-map <path.json>]");
        Console.WriteLine("            [--dlg-dir <dir>] [--cre-dir <dir>] [--strrefs <path.json>]");
        Console.WriteLine("            [--rate -10..10] [--volume 0..100] [--ogg] [--ffmpeg <path>] [--ogg-quality 0..10]");
        Console.WriteLine("            [--prefix <2 chars>] [--encoding <name>] [--min-length <n>]");
        Console.WriteLine("            [--config <path>] [--limit <n>] [--dry-run]");
        Console.WriteLine("      Synthesize a WAV for every unvoiced-but-has-text line and patch its");        Console.WriteLine("      SoundResRef into dialog.tlk in place. A .bak copy of the original TLK is");
        Console.WriteLine("      created automatically before the first write.");
        Console.WriteLine();
        Console.WriteLine("      --ogg transcodes each line to Ogg Vorbis via ffmpeg right after synthesis,");
        Console.WriteLine("      replacing the file in place under the same .wav filename - same trick Voices");
        Console.WriteLine("      Voices Extravaganza uses, since EE sniffs the actual audio format rather than");
        Console.WriteLine("      trusting the extension. Needs ffmpeg on PATH, or pass --ffmpeg <path-to-exe>.");
        Console.WriteLine("      --ogg-quality is libvorbis's 0-10 quality scale (higher = bigger/better; ~2 is");
        Console.WriteLine("      a reasonable starting point for voice lines - listen and adjust).");
        Console.WriteLine();
        Console.WriteLine("      --dry-run still writes real WAV files to --override (so you can listen and");
        Console.WriteLine("      check the voice/quality) but never touches dialog.tlk and never writes the");
        Console.WriteLine("      manifest. Combine with --limit so it doesn't render every candidate.");
        Console.WriteLine();
        Console.WriteLine("      --default-voice (alias: --voice) sets the fallback used when gender can't be");
        Console.WriteLine("      resolved. It can also be the literal word \"male\" or \"female\" to just reuse");
        Console.WriteLine("      whichever of --male-voice/--female-voice you already set, instead of typing the");
        Console.WriteLine("      same voice name twice.");
        Console.WriteLine();
        Console.WriteLine("      --gender-map points to a small hand-maintained JSON file mapping StrRef");
        Console.WriteLine("      numbers to \"M\"/\"F\", e.g. { \"12345\": \"F\", \"67890\": \"M\" } - a manual override,");
        Console.WriteLine("      checked first, for individual lines you want to force regardless of speaker.");
        Console.WriteLine();
        Console.WriteLine("      --speaker-map/--name-gender-map are the two files 'speakers' produces - checked");
        Console.WriteLine("      second, so any review/corrections you made there take priority over the live");
        Console.WriteLine("      lookup below.");
        Console.WriteLine();
        Console.WriteLine("      --cre-dir enables automatic gendering live, checked last: every *.dlg under");
        Console.WriteLine("      --dlg-dir (defaults to --override) is scanned to find each line's speaking NPC,");
        Console.WriteLine("      then a same-named .CRE under --cre-dir is located (mass-export both with Near");
        Console.WriteLine("      Infinity) and its Sex byte read. Best-effort name match, not guaranteed-correct.");
        Console.WriteLine();
        Console.WriteLine("      Anything still unresolved after all three falls back to --voice/--default-voice.");
        Console.WriteLine();
        Console.WriteLine("      Unmatched lines (no known speaker in --speaker-map or --dlg-dir scan) are");
        Console.WriteLine("      always excluded - non-dialogue TLK entries like item/spell descriptions and");
        Console.WriteLine("      journal text would break the game if voiced. The DLG scan always runs.");
        Console.WriteLine();
        Console.WriteLine("      --strrefs points to a flat JSON array of StrRef numbers, e.g. [12345, 67890],");
        Console.WriteLine("      to (re)generate exactly those lines. Listed lines are processed even if already");
        Console.WriteLine("      voiced, and always resynthesized fresh. --limit is ignored when --strrefs is given.");
        Console.WriteLine();
        Console.WriteLine("      PC name, race, and gender-token choices are set in patcher-config.json");
        Console.WriteLine("      (pcName, pcRace, pcGender). Pass --config to use a different file;");
        Console.WriteLine("      otherwise patcher-config.json next to the exe is required.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- run --game-dir \"C:\\Relax\\BGEET\" --ogg");
        Console.WriteLine("  dotnet run -- run --game-dir \"C:\\Relax\\BGEET\" --male-voice \"Microsoft David Desktop\" --female-voice \"Microsoft Hazel Desktop\" --ogg --cre-dir \"C:\\Relax\\BGEET\\AllCre\"");
        Console.WriteLine("  dotnet run -- generate --tlk \"D:\\Games\\BG2EE\\lang\\en_US\\dialog.tlk\" --override \"D:\\Games\\BG2EE\\override\" --speaker-map \"...\\speaker-strrefs.json\" --limit 50 --dry-run");
    }
}