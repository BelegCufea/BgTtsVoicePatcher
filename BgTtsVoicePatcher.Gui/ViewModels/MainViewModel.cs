using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using BgTtsVoicePatcher.Config;
using BgTtsVoicePatcher.Gui.Engine;
using BgTtsVoicePatcher.Gui.Models;
using BgTtsVoicePatcher.Speech;
using BgTtsVoicePatcher.State;
using BgTtsVoicePatcher.Text;
using Microsoft.Win32;

namespace BgTtsVoicePatcher.Gui.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PipelineRunner _runner = new();
    private CancellationTokenSource? _cts;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ==================================================================
    // Step 1: Game directory + TLK selection
    // ==================================================================

    private string _gameDir = "";
    public string GameDir
    {
        get => _gameDir;
        set { Set(ref _gameDir, value); RunCommand.RaiseCanExecuteChanged(); }
    }

    public ObservableCollection<string> DiscoveredTlkFiles { get; } = new();

    private string? _selectedTlkPath;
    public string? SelectedTlkPath
    {
        get => _selectedTlkPath;
        set
        {
            Set(ref _selectedTlkPath, value);
            RunCommand.RaiseCanExecuteChanged();
            RunSpeakersCommand.RaiseCanExecuteChanged();
            if (!string.IsNullOrWhiteSpace(value))
                LoadConfigForTlk(value);
        }
    }

    public EncodingInfo[]? AvailableEncodings { get; } = TlkEncodings.GetAvailableEncodings();

    public EncodingInfo? SelectedEncoding { get; set; } =
        TlkEncodings.GetAvailableEncodings()!
            .First(e => e.CodePage == Encoding.UTF8.CodePage);

    public RelayCommand BrowseGameDirCommand { get; }
    public RelayCommand ScanForTlkCommand { get; }
    public RelayCommand BrowseTlkCommand { get; }

    private void BrowseGameDir()
    {
        BrowseFolder(v =>
        {
            GameDir = v;
            ScanForTlk();
        });
    }

    private void ScanForTlk()
    {
        DiscoveredTlkFiles.Clear();
        if (string.IsNullOrWhiteSpace(GameDir) || !Directory.Exists(GameDir))
        {
            AppendLog("Game directory doesn't exist yet - nothing to scan.");
            return;
        }

        var found = TlkLocator.FindDialogTlkFiles(GameDir);
        foreach (var path in found)
            DiscoveredTlkFiles.Add(path);

        AppendLog(found.Count == 0
            ? $"No dialog.tlk found under {Path.Combine(GameDir, "lang")}. Use Browse to pick one manually."
            : $"Found {found.Count} dialog.tlk file(s).");

        if (found.Count > 0 && string.IsNullOrWhiteSpace(SelectedTlkPath))
            SelectedTlkPath = found.FirstOrDefault(s => s.Contains("en_US")) ?? found[0];
    }

    private void BrowseTlk()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select dialog.tlk",
            Filter = "TLK files (dialog.tlk)|dialog.tlk|All files (*.*)|*.*",
            FileName = "dialog.tlk"
        };
        if (dialog.ShowDialog() == true)
        {
            if (!DiscoveredTlkFiles.Contains(dialog.FileName))
                DiscoveredTlkFiles.Add(dialog.FileName);
            SelectedTlkPath = dialog.FileName;
        }
    }

    // ==================================================================
    // Step 2: DLG / CRE folders
    // ==================================================================

    private string _dlgDir = "";
    public string DlgDir { get => _dlgDir; set => Set(ref _dlgDir, value); }

    private string _creDir = "";
    public string CreDir { get => _creDir; set => Set(ref _creDir, value); }

    public RelayCommand BrowseDlgDirCommand { get; }
    public RelayCommand BrowseCreDirCommand { get; }

    // ==================================================================
    // Step 3: patcher-config.json editing
    // ==================================================================

    private string _configPath = "";
    public string ConfigPath { get => _configPath; set => Set(ref _configPath, value); }

    private string _configStatusText = "No dialog.tlk selected yet.";
    public string ConfigStatusText { get => _configStatusText; set => Set(ref _configStatusText, value); }

    private string _pcName = "friend";
    public string PcName { get => _pcName; set => Set(ref _pcName, value); }

    private string _pcRace = "human";
    public string PcRace { get => _pcRace; set => Set(ref _pcRace, value); }

    public ObservableCollection<string> PcGenderOptions { get; } = new() { "male", "female", "neutral" };

    private string _pcGender = "neutral";
    public string PcGender { get => _pcGender; set => Set(ref _pcGender, value); }

    public ObservableCollection<KeyValueRow> CreNameReplacementsRows { get; } = new();
    public ObservableCollection<KeyValueRow> GenderOverridesRows { get; } = new();
    public ObservableCollection<GenderTokenRow> GenderTokenRows { get; } = new();
    public ObservableCollection<TokenRow> IdentityTokenRows { get; } = new();
    public ObservableCollection<PhoneticRuleRow> PhoneticRuleRows { get; } = new();

    public RelayCommand BrowseConfigCommand { get; }
    public RelayCommand ReloadConfigCommand { get; }
    public RelayCommand SaveConfigCommand { get; }

    private void LoadConfigForTlk(string tlkPath)
    {
        try
        {
            ConfigPath = ConfigService.ResolveConfigPath(tlkPath);
            LoadConfigFromPath(ConfigPath);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load config: {ex.Message}");
        }
    }

    private void LoadConfigFromPath(string path)
    {
        var config = ConfigService.Load(path);

        PcName = config.PcName;
        PcRace = config.PcRace;
        PcGender = config.PcGender;

        CreNameReplacementsRows.Clear();
        foreach (var (k, v) in config.CreNameReplacements)
            CreNameReplacementsRows.Add(new KeyValueRow { Key = k, Value = v });

        GenderOverridesRows.Clear();
        foreach (var (k, v) in config.GenderOverrides)
            GenderOverridesRows.Add(new KeyValueRow { Key = k, Value = v });

        GenderTokenRows.Clear();
        foreach (var (token, values) in config.GenderTokens)
            GenderTokenRows.Add(new GenderTokenRow { Token = token, Male = values.Male, Female = values.Female, Neutral = values.Neutral });

        IdentityTokenRows.Clear();
        foreach (var token in config.IdentityTokens)
            IdentityTokenRows.Add(new TokenRow { Value = token });

        PhoneticRuleRows.Clear();
        foreach (var rule in config.PhoneticRules)
            PhoneticRuleRows.Add(new PhoneticRuleRow { Pattern = rule.Pattern, Replacement = rule.Replacement, Comment = rule.Comment ?? "" });

        var isBundled = path == PatcherConfig.DefaultConfigPath();
        ConfigStatusText = isBundled
            ? $"Loaded bundled default config (none found beside the TLK yet). Will save to: {ConfigService.GetSavePathFor(SelectedTlkPath ?? path)}"
            : $"Loaded config from: {path}";
    }

    private void BrowseConfig()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select patcher-config.json",
            Filter = "JSON config (*.json)|*.json|All files (*.*)|*.*",
            FileName = "patcher-config.json"
        };
        if (dialog.ShowDialog() == true)
        {
            ConfigPath = dialog.FileName;
            LoadConfigFromPath(ConfigPath);
        }
    }

    private void SaveConfig()
    {
        if (string.IsNullOrWhiteSpace(SelectedTlkPath))
        {
            AppendLog("Select a dialog.tlk first so there's somewhere to save the config beside.");
            return;
        }

        var savePath = ConfigService.GetSavePathFor(SelectedTlkPath);
        try
        {
            ConfigService.Save(savePath, PcName, PcRace, PcGender,
                CreNameReplacementsRows, GenderOverridesRows, GenderTokenRows, IdentityTokenRows, PhoneticRuleRows);
            ConfigPath = savePath;
            ConfigStatusText = $"Saved to: {savePath}";
            AppendLog($"Config saved: {savePath}");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save config: {ex.Message}");
        }
    }

    // ==================================================================
    // Step 4: Voices + Run Speakers
    // ==================================================================

    public ObservableCollection<string> AvailableVoices { get; } = new();

    private string? _selectedVoice;
    public string? SelectedVoice { get => _selectedVoice; set => Set(ref _selectedVoice, value); }

    private string? _selectedMaleVoice;
    public string? SelectedMaleVoice { get => _selectedMaleVoice; set => Set(ref _selectedMaleVoice, value); }

    private string? _selectedFemaleVoice;
    public string? SelectedFemaleVoice { get => _selectedFemaleVoice; set => Set(ref _selectedFemaleVoice, value); }

    public RelayCommand RefreshVoicesCommand { get; }
    public RelayCommand RunSpeakersCommand { get; }

    private void RefreshVoices()
    {
        AvailableVoices.Clear();
        try
        {
            foreach (var v in VoiceSynthesizer.ListInstalledVoices())
                AvailableVoices.Add(v);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not list SAPI voices: {ex.Message}");
        }
    }

    private static string? ExtractVoiceName(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        var idx = display.IndexOf("  (", StringComparison.Ordinal);
        return idx > 0 ? display[..idx].Trim() : display.Trim();
    }

    private async Task RunSpeakersAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedTlkPath))
        {
            AppendLog("Select a dialog.tlk first.");
            return;
        }

        var options = BuildOptions();

        await RunGuarded(async (log, _, ct) =>
        {
            await _runner.RunSpeakersOnlyAsync(options, log, ct);
        }, "Speaker resolution finished.");

        await LoadSpeakerReviewAsync();
    }

    // ==================================================================
    // Step 5: Speaker Review grid
    // ==================================================================

    public BulkObservableCollection<SpeakerRowViewModel> SpeakerRows { get; } = new();
    // static, not an instance property: DataGridColumn isn't part of the visual or
    // logical tree, so a RelativeSource binding on a column can never resolve back
    // to the Window's DataContext - x:Static is the reliable way to feed a fixed
    // list into a DataGridComboBoxColumn's ItemsSource.
    public static readonly string[] SpeakerGenderOptions = { "", "M", "F" };
    public static readonly string[] GenderFilterOptions = { "All", "M", "F", "(blank)" };
    public static readonly string[] VoicedFilterOptions = { "All", "Yes", "No" };

    private ICollectionView? _speakerRowsView;
    public ICollectionView SpeakerRowsView => _speakerRowsView ??= BuildSpeakerRowsView();

    private ICollectionView BuildSpeakerRowsView()
    {
        var view = CollectionViewSource.GetDefaultView(SpeakerRows);
        view.Filter = FilterSpeakerRow;
        return view;
    }

    // ---- Excel-like per-column filters --------------------------------

    private string _filterStrRef = "";
    public string FilterStrRef { get => _filterStrRef; set { Set(ref _filterStrRef, value); SpeakerRowsView.Refresh(); } }

    private string _filterSystemName = "";
    public string FilterSystemName { get => _filterSystemName; set { Set(ref _filterSystemName, value); SpeakerRowsView.Refresh(); } }

    private string _filterRealName = "";
    public string FilterRealName { get => _filterRealName; set { Set(ref _filterRealName, value); SpeakerRowsView.Refresh(); } }

    private string _filterGender = "All";
    public string FilterGender { get => _filterGender; set { Set(ref _filterGender, value); SpeakerRowsView.Refresh(); } }

    private string _filterVoiced = "All";
    public string FilterVoiced { get => _filterVoiced; set { Set(ref _filterVoiced, value); SpeakerRowsView.Refresh(); } }

    private string _filterText = "";
    public string FilterText { get => _filterText; set { Set(ref _filterText, value); SpeakerRowsView.Refresh(); } }

    public RelayCommand ClearFiltersCommand { get; }

    private void ClearFilters()
    {
        FilterStrRef = "";
        FilterSystemName = "";
        FilterRealName = "";
        FilterGender = "All";
        FilterVoiced = "All";
        FilterText = "";
    }

    private bool FilterSpeakerRow(object obj)
    {
        if (obj is not SpeakerRowViewModel row)
            return true;

        if (!string.IsNullOrWhiteSpace(FilterStrRef) &&
            !row.StrRef.ToString(CultureInfo.InvariantCulture).Contains(FilterStrRef.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(FilterSystemName) &&
            !(row.SystemName?.Contains(FilterSystemName.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
            return false;

        if (!string.IsNullOrWhiteSpace(FilterRealName) &&
            !(row.RealName?.Contains(FilterRealName.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
            return false;

        if (FilterGender != "All")
        {
            var wantsBlank = FilterGender == "(blank)";
            if (wantsBlank ? !string.IsNullOrEmpty(row.Gender) : !string.Equals(row.Gender, FilterGender, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (FilterVoiced != "All")
        {
            var wantsVoiced = FilterVoiced == "Yes";
            if (row.HasSound != wantsVoiced)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var needle = FilterText.Trim();
            var matchesText = row.RawText.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || row.CleanedText.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (!matchesText)
                return false;
        }

        return true;
    }

    public RelayCommand ReloadSpeakerReviewCommand { get; }
    public RelayCommand SaveSpeakerChangesCommand { get; }

    private async Task LoadSpeakerReviewAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedTlkPath))
        {
            AppendLog("Select a dialog.tlk first.");
            return;
        }

        var options = BuildOptions();

        await RunGuarded(async (log, _, ct) =>
        {
            List<SpeakerReviewRow> rows = await _runner.BuildSpeakerReviewRowsAsync(options, log, ct);

            log.Report($"Building {rows.Count} review row(s)...");
            var speakerRows = await Task.Run(() => rows
                .Select(r => new SpeakerRowViewModel(
                    r.StrRef, r.SystemName, r.RealName, r.Gender, r.HasSound, r.SoundResRef,
                    r.RawText, r.CleanedText, r.IsTextOverridden))
                .ToList(), ct);

            SpeakerRows.ReplaceAll(speakerRows);
            log.Report($"Loaded {rows.Count} row(s) into the review grid.");
        }, "Speaker review loaded.");
    }

    private void SaveSpeakerChanges()
    {
        if (string.IsNullOrWhiteSpace(SelectedTlkPath))
        {
            AppendLog("Select a dialog.tlk first.");
            return;
        }

        var options = BuildOptions();
        var dirtyGender = SpeakerRows.Where(r => r.IsGenderDirty && r.SystemName is not null).ToList();
        var dirtyText = SpeakerRows.Where(r => r.IsTextDirty).ToList();

        if (dirtyGender.Count == 0 && dirtyText.Count == 0)
        {
            AppendLog("No changes to save.");
            return;
        }

        try
        {
            if (dirtyGender.Count > 0)
            {
                var updates = dirtyGender.ToDictionary(
                    r => r.SystemName!,
                    r => string.IsNullOrWhiteSpace(r.Gender) ? null : r.Gender,
                    StringComparer.OrdinalIgnoreCase);

                SpeakerNamesEditor.ApplyGenderUpdates(options.SpeakerNamesPath, updates);
                AppendLog($"Saved {dirtyGender.Count} gender correction(s) to {options.SpeakerNamesPath}.");
            }

            if (dirtyText.Count > 0)
            {
                var textOverrides = TextOverrides.Load(options.TextOverridesPath);
                foreach (var row in dirtyText)
                    textOverrides[row.StrRef] = row.CleanedText;

                TextOverrides.Save(options.TextOverridesPath, textOverrides);
                AppendLog($"Saved {dirtyText.Count} text override(s) to {options.TextOverridesPath}. " +
                          "These will be used verbatim next time you Run - no re-cleaning.");
            }

            foreach (var row in dirtyGender.Concat(dirtyText).Distinct())
                row.MarkSaved();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save speaker changes: {ex.Message}");
        }
    }

    // ==================================================================
    // Step 6: Sound options
    // ==================================================================

    private bool _useOgg = true;
    public bool UseOgg { get => _useOgg; set => Set(ref _useOgg, value); }

    private string _ffmpegPath = "ffmpeg";
    public string FfmpegPath { get => _ffmpegPath; set => Set(ref _ffmpegPath, value); }

    private int _oggQuality = 2;
    public int OggQuality { get => _oggQuality; set => Set(ref _oggQuality, value); }

    private int _rate;
    public int Rate { get => _rate; set => Set(ref _rate, value); }

    private int _volume = 100;
    public int Volume { get => _volume; set => Set(ref _volume, value); }

    private string _prefix = "TS";
    public string Prefix { get => _prefix; set => Set(ref _prefix, value); }

    // ==================================================================
    // Step 7: Limit / dry-run / final run
    // ==================================================================

    private string _limitText = "";
    public string LimitText { get => _limitText; set => Set(ref _limitText, value); }

    private bool _dryRun;
    public bool DryRun { get => _dryRun; set => Set(ref _dryRun, value); }

    public RelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ==================================================================
    // Wizard navigation
    // ==================================================================

    private int _currentStepIndex;
    public int CurrentStepIndex { get => _currentStepIndex; set => Set(ref _currentStepIndex, value); }

    public RelayCommand NextStepCommand { get; }
    public RelayCommand PreviousStepCommand { get; }

    private const int StepCount = 5;

    // ==================================================================
    // Shared run state / log
    // ==================================================================

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            Set(ref _isRunning, value);
            RunCommand.RaiseCanExecuteChanged();
            RunSpeakersCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private string _statusText = "Ready.";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => Set(ref _progressValue, value); }

    private double _progressMax = 100;
    public double ProgressMax { get => _progressMax; set => Set(ref _progressMax, value); }

    private bool _isProgressIndeterminate;
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; set => Set(ref _isProgressIndeterminate, value); }

    public ObservableCollection<string> LogLines { get; } = new();

    private void AppendLog(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > 5000)
            LogLines.RemoveAt(0);
    }

    // ==================================================================
    // Construction
    // ==================================================================

    public MainViewModel()
    {
        BrowseGameDirCommand = new RelayCommand(BrowseGameDir);
        ScanForTlkCommand = new RelayCommand(ScanForTlk);
        BrowseTlkCommand = new RelayCommand(BrowseTlk);

        BrowseDlgDirCommand = new RelayCommand(() => BrowseFolder(v => DlgDir = v));
        BrowseCreDirCommand = new RelayCommand(() => BrowseFolder(v => CreDir = v));

        BrowseConfigCommand = new RelayCommand(BrowseConfig);
        ReloadConfigCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(ConfigPath) && File.Exists(ConfigPath))
                LoadConfigFromPath(ConfigPath);
        });
        SaveConfigCommand = new RelayCommand(SaveConfig);

        RefreshVoicesCommand = new RelayCommand(RefreshVoices);
        RunSpeakersCommand = new RelayCommand(
            async () => await RunSpeakersAsync(),
            () => !IsRunning && !string.IsNullOrWhiteSpace(SelectedTlkPath));

        ReloadSpeakerReviewCommand = new RelayCommand(async () => await LoadSpeakerReviewAsync());
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        SaveSpeakerChangesCommand = new RelayCommand(SaveSpeakerChanges);

        RunCommand = new RelayCommand(
            async () => await RunFullPipelineAsync(),
            () => !IsRunning && !string.IsNullOrWhiteSpace(SelectedTlkPath));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);

        NextStepCommand = new RelayCommand(() => CurrentStepIndex = Math.Min(CurrentStepIndex + 1, StepCount - 1));
        PreviousStepCommand = new RelayCommand(() => CurrentStepIndex = Math.Max(CurrentStepIndex - 1, 0));

        RefreshVoices();
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static void BrowseFolder(Action<string> onSelected)
    {
        var dialog = new OpenFolderDialog { Title = "Select folder" };
        if (dialog.ShowDialog() == true)
            onSelected(dialog.FolderName);
    }

    private PipelineOptions BuildOptions()
    {
        var limit = int.MaxValue;
        if (!string.IsNullOrWhiteSpace(LimitText))
            int.TryParse(LimitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
        if (limit <= 0) limit = int.MaxValue;

        return new PipelineOptions
        {
            GameDir = GameDir,
            TlkPath = SelectedTlkPath!,
            Encoding = SelectedEncoding?.GetEncoding() ?? Encoding.UTF8,
            DlgDir = string.IsNullOrWhiteSpace(DlgDir) ? null : DlgDir,
            CreDir = string.IsNullOrWhiteSpace(CreDir) ? null : CreDir,
            ConfigPath = string.IsNullOrWhiteSpace(ConfigPath) ? null : ConfigPath,
            VoiceName = ExtractVoiceName(SelectedVoice),
            MaleVoice = ExtractVoiceName(SelectedMaleVoice),
            FemaleVoice = ExtractVoiceName(SelectedFemaleVoice),
            UseOgg = UseOgg,
            FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath,
            OggQuality = OggQuality,
            Rate = Rate,
            Volume = Volume,
            Prefix = string.IsNullOrWhiteSpace(Prefix) ? "TS" : Prefix,
            Limit = limit,
            DryRun = DryRun,
        };
    }

    /// <summary>Runs a long operation with shared IsRunning/progress/log/cancellation
    /// plumbing, so every wizard step's action button behaves consistently.</summary>
    private async Task RunGuarded(Func<IProgress<string>, IProgress<PipelineProgress>, CancellationToken, Task> action, string doneMessage)
    {
        IsRunning = true;
        StatusText = "Running...";
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        _cts = new CancellationTokenSource();

        var log = new Progress<string>(AppendLog);
        var progress = new Progress<PipelineProgress>(p =>
        {
            IsProgressIndeterminate = false;
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            var baseText = $"{p.Done}/{p.Total} (ok {p.Generated + p.Reused}, failed {p.Failed})";

            if (p.RemainingTime.HasValue)
            {
                var time = p.RemainingTime.Value;
                string timeStr;

                if (time.TotalDays >= 1)
                {
                    // Format: 2d 14h 35m
                    timeStr = $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m";
                }
                else if (time.TotalHours >= 1)
                {
                    // Format: 5h 23m 10s
                    timeStr = $"{time.Hours}h {time.Minutes}m {time.Seconds}s";
                }
                else if (time.TotalMinutes >= 1)
                {
                    // Format: 12m 04s
                    timeStr = $"{time.Minutes}m {time.Seconds:D2}s";
                }
                else
                {
                    // Format: 45s
                    timeStr = $"{time.Seconds}s";
                }

                StatusText = $"{baseText} — Remaining: {timeStr}";
            }
            else
            {
                StatusText = baseText;
            }
        });

        try
        {
            await action(log, progress, _cts.Token);
            StatusText = doneMessage;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            AppendLog("Cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed — see log.";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunFullPipelineAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedTlkPath))
        {
            AppendLog("Select a dialog.tlk first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedVoice) &&
            string.IsNullOrWhiteSpace(SelectedMaleVoice) &&
            string.IsNullOrWhiteSpace(SelectedFemaleVoice))
        {
            AppendLog("Select at least one voice (default, male, or female) before running.");
            return;
        }

        // Speaker files were already produced by the "Run Speakers" step if the
        // user followed the wizard in order; if they jump straight here, generate
        // will fall back to a live DLG/CRE scan on its own (RunAsync handles this).
        var options = BuildOptions();

        LogLines.Clear();
        await RunGuarded(async (log, progress, ct) =>
        {
            var result = await _runner.RunAsync(options, log, progress, ct);
            if (!result.Success)
                throw new InvalidOperationException(result.Message);
        }, "Run completed.");
    }
}
