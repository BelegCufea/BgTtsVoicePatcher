using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgTtsVoicePatcher.Audio;
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
        var charname = options.GetValueOrDefault("charname", "friend");

        var tlk = TlkFile.Load(tlkPath, encoding);
        var cleaner = new DialogTextCleaner(charNameReplacement: charname);

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
        var charname = options.GetValueOrDefault("charname", "friend");
        var outMap = options.GetValueOrDefault("out-map", System.IO.Path.Combine(dlgDir, "speaker-strrefs.json"));
        var outNames = options.GetValueOrDefault("out-names", System.IO.Path.Combine(dlgDir, "speaker-names.json"));
        var outUnmatched = options.GetValueOrDefault("out-unmatched", System.IO.Path.Combine(dlgDir, "speaker-unmatched.txt"));

        var tlk = TlkFile.Load(tlkPath, encoding);
        var cleaner = new DialogTextCleaner(charNameReplacement: charname);
        var candidates = GetTtsCandidates(tlk, cleaner, minLength, int.MaxValue);
        var candidateStrRefs = candidates.Select(c => c.Entry.StrRef).ToHashSet();

        Console.WriteLine($"Scanning *.dlg in: {dlgDir}");
        var scan = SpeakerIndex.Scan(dlgDir);
        Console.WriteLine($"  Files scanned: {scan.FilesScanned}, failed to parse: {scan.FilesFailed}");

        var relevantMap = scan.StrRefToSpeaker
            .Where(kv => candidateStrRefs.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var relevantNames = relevantMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        CreGenderLookup? creLookup = string.IsNullOrWhiteSpace(creDir) ? null : new CreGenderLookup(creDir);
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

        var unmatched = candidates
            .Where(c => !relevantMap.ContainsKey(c.Entry.StrRef))
            .OrderBy(c => c.Entry.StrRef)
            .ToList();

        File.WriteAllLines(outUnmatched, unmatched.Select(c => $"[{c.Entry.StrRef}] {c.CleanedText}"));

        Console.WriteLine($"  TTS candidates with a known speaker: {relevantMap.Count} / {candidateStrRefs.Count}");
        Console.WriteLine($"  Distinct speaker names:    {relevantNames.Count}");
        if (creLookup is not null)
            Console.WriteLine($"  Gendered via CRE Sex byte: {resolvedViaCre} / {relevantNames.Count}");
        Console.WriteLine($"  No speaker found:          {unmatched.Count} -> {outUnmatched}");
        Console.WriteLine("    (often item/spell descriptions, journal text, etc. - not every line is");
        Console.WriteLine("     spoken dialogue, so this list isn't necessarily a gap. Worth a skim though.)");
        Console.WriteLine($"  Wrote: {outMap}");
        Console.WriteLine($"  Wrote/updated: {outNames}");
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
        var cleaner = new DialogTextCleaner();

        // File-based names/genders (from 'speakers'), live ones (--dlg-dir/--cre-dir),
        // or neither - either source is optional, both can be given together (file
        // takes priority per StrRef, same as 'generate').
        Dictionary<int, string>? fileSpeakerMap = null;
        SpeakerGenderMap? fileGenderMap = null;
        var speakerMapPath = options.GetValueOrDefault("speaker-map");
        var nameGenderMapPath = options.GetValueOrDefault("name-gender-map");
        if (!string.IsNullOrWhiteSpace(speakerMapPath) || !string.IsNullOrWhiteSpace(nameGenderMapPath))
        {
            fileSpeakerMap = SpeakerIndex.LoadStrRefMap(speakerMapPath ?? string.Empty);
            fileGenderMap = SpeakerGenderMap.Load(nameGenderMapPath);
        }

        Dictionary<int, string>? liveSpeakerMap = null;
        CreGenderLookup? liveCreLookup = null;
        var dlgDir = options.GetValueOrDefault("dlg-dir");
        var creDir = options.GetValueOrDefault("cre-dir");
        if (!string.IsNullOrWhiteSpace(dlgDir))
        {
            Console.WriteLine($"Scanning *.dlg in: {dlgDir}");
            var dlgScan = SpeakerIndex.Scan(dlgDir);
            Console.WriteLine($"  Files scanned: {dlgScan.FilesScanned}, failed to parse: {dlgScan.FilesFailed}");
            liveSpeakerMap = dlgScan.StrRefToSpeaker;

            if (!string.IsNullOrWhiteSpace(creDir))
                liveCreLookup = new CreGenderLookup(creDir);
        }

        var rows = new List<DialogReportRow>();

        foreach (var entry in tlk.Entries)
        {
            if (!entry.HasText)
                continue;

            string? systemName = null;
            string? realName = null;
            var gender = Gender.Unknown;

            if (fileSpeakerMap is not null && fileSpeakerMap.TryGetValue(entry.StrRef, out var fileName))
            {
                systemName = fileName;
                gender = fileGenderMap!.Get(fileName);
                realName = fileGenderMap.GetDisplayName(fileName);
            }
            else if (liveSpeakerMap is not null && liveSpeakerMap.TryGetValue(entry.StrRef, out var liveName))
            {
                systemName = liveName;
                if (liveCreLookup is not null)
                {
                    var info = liveCreLookup.ResolveInfo(liveName);
                    gender = info?.Gender ?? Gender.Unknown;
                    realName = ResolveDisplayName(info?.NameStrRef, tlk, cleaner);
                }
            }

            // A "display name" that's just the system name echoed back means nothing
            // real was resolved (the fallback grouping in speakers writes that) -
            // leave it blank rather than imply we found something we didn't.
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
        TlkFile tlk, DialogTextCleaner cleaner, int minLength, int limit, ISet<int>? requireKnownSpeaker = null)
    {
        var candidates = new List<(TlkEntry Entry, string CleanedText)>();
        foreach (var entry in tlk.Entries)
        {
            if (!entry.HasText || entry.HasSound) continue;
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

        var manifestPath = options.GetValueOrDefault(
            "manifest",
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(tlkPath))!, "tts-manifest.json"));

        var voiceName = options.GetValueOrDefault("voice") ?? options.GetValueOrDefault("default-voice");
        var maleVoice = options.GetValueOrDefault("male-voice");
        var femaleVoice = options.GetValueOrDefault("female-voice");
        var genderMap = GenderMap.Load(options.GetValueOrDefault("gender-map"));
        var fileSpeakerMap = SpeakerIndex.LoadStrRefMap(options.GetValueOrDefault("speaker-map") ?? string.Empty);
        var fileGenderMap = SpeakerGenderMap.Load(options.GetValueOrDefault("name-gender-map"));

        var dlgDir = options.GetValueOrDefault("dlg-dir", overrideDir);
        var creDir = options.GetValueOrDefault("cre-dir");
        var skipUnmatched = options.ContainsKey("skip-unmatched");
        var liveSpeakerMap = new Dictionary<int, string>();
        CreGenderLookup? liveCreLookup = null;

        if (!string.IsNullOrWhiteSpace(creDir) || skipUnmatched)
        {
            Console.WriteLine($"Scanning *.dlg in: {dlgDir}");
            var dlgScan = SpeakerIndex.Scan(dlgDir);
            Console.WriteLine($"  Files scanned: {dlgScan.FilesScanned}, failed to parse: {dlgScan.FilesFailed}");
            liveSpeakerMap = dlgScan.StrRefToSpeaker;

            if (!string.IsNullOrWhiteSpace(creDir))
                liveCreLookup = new CreGenderLookup(creDir);
        }

        // --skip-unmatched: a line counts as "matched" if either the file-based
        // speaker map (--speaker-map) or the live DLG scan found a speaker for it.
        var requireKnownSpeaker = skipUnmatched
            ? new HashSet<int>(fileSpeakerMap.Keys.Concat(liveSpeakerMap.Keys))
            : null;

        var rate = int.Parse(options.GetValueOrDefault("rate", "0"), CultureInfo.InvariantCulture);
        var volume = int.Parse(options.GetValueOrDefault("volume", "100"), CultureInfo.InvariantCulture);
        var prefix = options.GetValueOrDefault("prefix", "TS").ToUpperInvariant();
        var encoding = ResolveEncoding(options.GetValueOrDefault("encoding", "windows-1252"));
        var minLength = int.Parse(options.GetValueOrDefault("min-length", "2"), CultureInfo.InvariantCulture);
        var charname = options.GetValueOrDefault("charname", "friend");
        var limit = int.Parse(options.GetValueOrDefault("limit", int.MaxValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var dryRun = options.ContainsKey("dry-run");

        Directory.CreateDirectory(overrideDir);

        var tlk = TlkFile.Load(tlkPath, encoding);
        var cleaner = new DialogTextCleaner(charNameReplacement: charname);

        var strRefFilter = LoadStrRefFilter(options.GetValueOrDefault("strrefs"));

        var candidates = strRefFilter is not null
            ? GetCandidatesForStrRefs(tlk, cleaner, minLength, strRefFilter)
            : GetTtsCandidates(tlk, cleaner, minLength, limit, requireKnownSpeaker);

        Console.WriteLine(strRefFilter is not null
            ? $"{candidates.Count} line(s) selected from --strrefs ({strRefFilter.Count} requested)."
            : $"{candidates.Count} unvoiced line(s) selected (limit={limit}{(skipUnmatched ? ", unmatched skipped" : "")}).");

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
            var gender = ResolveGender(entry.StrRef, genderMap, fileSpeakerMap, fileGenderMap, liveSpeakerMap, liveCreLookup);
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
                var voiceUsed = synth.VoiceNameFor(ResolveGender(entry.StrRef, genderMap, fileSpeakerMap, fileGenderMap, liveSpeakerMap, liveCreLookup));
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

    private static Gender ResolveGender(
        int strRef,
        GenderMap genderMap,
        Dictionary<int, string> fileSpeakerMap, SpeakerGenderMap fileGenderMap,
        Dictionary<int, string> liveSpeakerMap, CreGenderLookup? liveCreLookup)
    {
        var gender = genderMap.Get(strRef);
        if (gender != Gender.Unknown)
            return gender;

        if (fileSpeakerMap.TryGetValue(strRef, out var fileName))
        {
            gender = fileGenderMap.Get(fileName);
            if (gender != Gender.Unknown)
                return gender;
        }

        if (liveCreLookup is not null && liveSpeakerMap.TryGetValue(strRef, out var liveName))
            gender = liveCreLookup.Resolve(liveName);

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
        Console.WriteLine("  voices");
        Console.WriteLine("      List installed SAPI voices (NaturalVoiceSAPIAdapter voices show up here too).");
        Console.WriteLine();
        Console.WriteLine("  scan --tlk <path> [--encoding <name>] [--min-length <n>] [--charname <word>]");
        Console.WriteLine("      Report how many lines are already voiced vs. TTS candidates. Writes nothing.");
        Console.WriteLine();
        Console.WriteLine("  speakers --tlk <path> --dlg-dir <dir> [--cre-dir <dir>]");
        Console.WriteLine("            [--out-map <path>] [--out-names <path>] [--out-unmatched <path>]");
        Console.WriteLine("      Scans every *.dlg in --dlg-dir for NPC lines and writes:");
        Console.WriteLine("        speaker-strrefs.json   (auto-generated StrRef -> speaker name, don't edit)");
        Console.WriteLine("        speaker-names.json     (speaker names grouped by in-game display name, e.g.");
        Console.WriteLine("                                 { \"Jaheira\": { \"JAHEIRA\": \"F\", \"BJAHEIR\": \"F\" } } -");
        Console.WriteLine("                                 pre-filled from each speaker's CRE if --cre-dir is given");
        Console.WriteLine("                                 (display name from the CRE's own name StrRef, gender from");
        Console.WriteLine("                                 its Sex byte); open it to fill in any null or fix a wrong");
        Console.WriteLine("                                 guess. Reruns only add brand-new names, never touching");
        Console.WriteLine("                                 ones already in the file.");
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
        Console.WriteLine("            [--dlg-dir <dir>] [--cre-dir <dir>] [--skip-unmatched] [--strrefs <path.json>]");
        Console.WriteLine("            [--rate -10..10] [--volume 0..100] [--ogg] [--ffmpeg <path>] [--ogg-quality 0..10]");
        Console.WriteLine("            [--prefix <2 chars>] [--encoding <name>] [--min-length <n>]");
        Console.WriteLine("            [--charname <word>] [--limit <n>] [--dry-run]");
        Console.WriteLine("      Synthesize a WAV for every unvoiced-but-has-text line and patch its");
        Console.WriteLine("      SoundResRef into dialog.tlk in place. A .bak copy of the original TLK is");
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
        Console.WriteLine("      --skip-unmatched excludes lines with no known speaker at all (from either");
        Console.WriteLine("      --speaker-map or a live --dlg-dir scan) - the same set 'speakers' writes to");
        Console.WriteLine("      speaker-unmatched.txt, which is usually non-dialogue text like item/spell");
        Console.WriteLine("      descriptions rather than missed NPC lines. Triggers a --dlg-dir scan even");
        Console.WriteLine("      without --cre-dir. Not applied when --strrefs is given.");
        Console.WriteLine();
        Console.WriteLine("      --strrefs points to a flat JSON array of StrRef numbers, e.g. [12345, 67890],");
        Console.WriteLine("      to (re)generate exactly those lines instead of scanning the whole TLK. Unlike");
        Console.WriteLine("      the normal unvoiced-only selection, listed lines are processed even if they");
        Console.WriteLine("      already have sound, and always resynthesized fresh (the manifest's reuse check");
        Console.WriteLine("      is skipped) - this is for deliberately redoing specific lines, not just");
        Console.WriteLine("      catching up on new ones. --limit is ignored when --strrefs is given.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- scan --tlk \"D:\\Games\\BG2EE\\lang\\en_US\\dialog.tlk\"");
        Console.WriteLine("  dotnet run -- generate --tlk \"D:\\Games\\BG2EE\\lang\\en_US\\dialog.tlk\" --override \"D:\\Games\\BG2EE\\override\" --voice \"Microsoft David Desktop\" --limit 50 --dry-run");
    }
}
