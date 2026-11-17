using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Shell;
using System.Windows.Threading;
using PartCombiner.Core;

namespace PartCombiner;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _speedTimer;
    private readonly DispatcherTimer _queueTimer;
    private readonly List<string> _queuedPaths = [];
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private SevenZipEngine? _engine;
    private CancellationTokenSource? _cts;

    private string _outputFolder;
    private bool _extractArchives;
    private bool _recycleParts;
    private bool _openFolderWhenDone;
    private bool _isBusy;
    private bool _isScanning;
    private string _statusText = "";
    private bool _statusIsError;
    private double _overallProgress;
    private string _speedText = "";

    // Throughput bookkeeping for the speed / time-left readout.
    private long _runTotalBytes;
    private long _runProcessedBytes;
    private long _lastSampleBytes;
    private DateTime _lastSampleTime;
    private double _bytesPerSecond;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _outputFolder = settings.OutputFolder ?? "";
        _extractArchives = settings.ExtractArchives;
        _recycleParts = settings.RecycleParts;
        _openFolderWhenDone = settings.OpenFolderWhenDone;
        _engine = SevenZipEngine.Locate(settings.SevenZipPath);

        Items.CollectionChanged += (_, _) => RaiseCounts();

        RemoveCommand = new RelayCommand(p =>
        {
            if (p is PartSetViewModel item && item.CanRemove)
                Items.Remove(item);
            if (!IsBusy) SetStatus(DefaultStatus());
        });
        RevealCommand = new RelayCommand(p =>
        {
            if (p is PartSetViewModel { OutputPath: { } path }) Reveal(path);
        });
        ToggleExpandCommand = new RelayCommand(p =>
        {
            if (p is PartSetViewModel item) item.IsExpanded = !item.IsExpanded;
        });

        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _speedTimer.Tick += (_, _) => UpdateSpeed();

        _queueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _queueTimer.Tick += (_, _) => FlushQueuedPaths();

        _statusText = DefaultStatus();

        Split = new SplitViewModel(settings, () => _engine);
        Split.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SplitViewModel.IsBusy)) Raise(nameof(CanSwitchMode)); };
        Split.Finished += (_, firstPart) => { if (_openFolderWhenDone) Reveal(firstPart); };
    }

    // ----- Combine / Split -----

    public SplitViewModel Split { get; }

    private bool _isSplitMode;

    public bool IsSplitMode
    {
        get => _isSplitMode;
        set { if (SetField(ref _isSplitMode, value)) Raise(nameof(IsCombineMode), nameof(DropText)); }
    }

    public string DropText => _isSplitMode ? "Drop to split" : "Drop to add";

    public bool IsCombineMode
    {
        get => !_isSplitMode;
        set => IsSplitMode = !value;
    }

    /// <summary>Stay on the current view while it's working.</summary>
    public bool CanSwitchMode => !IsBusy && !Split.IsBusy;

    public ObservableCollection<PartSetViewModel> Items { get; } = [];

    public RelayCommand RemoveCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand ToggleExpandCommand { get; }

    // ----- 7-Zip -----

    public bool EngineAvailable => _engine is not null;

    public string EngineText => _engine is null
        ? "7-Zip not found"
        : string.IsNullOrEmpty(_engine.Version) ? "7-Zip" : $"7-Zip {_engine.Version}";

    public string EngineTooltip => _engine is null
        ? "7-Zip is needed to extract RAR, 7z and ZIP archives (plain file joining works without it).\nInstall it from 7-zip.org, or click to locate 7z.exe."
        : $"Extracting with {_engine.ExecutablePath}\nClick to use a different 7z.exe.";

    public bool TrySetEngine(string path)
    {
        var engine = SevenZipEngine.FromPath(path);
        if (engine is null) return false;
        _engine = engine;
        _settings.SevenZipPath = engine.ExecutablePath;
        Raise(nameof(EngineAvailable), nameof(EngineText), nameof(EngineTooltip));
        RefreshItems();
        Split.EngineChanged();
        return true;
    }

    // ----- Options -----

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (SetField(ref _outputFolder, value ?? ""))
            {
                _settings.OutputFolder = string.IsNullOrEmpty(_outputFolder) ? null : _outputFolder;
                Raise(nameof(OutputFolderDisplay), nameof(HasCustomOutput));
            }
        }
    }

    public string OutputFolderDisplay => string.IsNullOrEmpty(_outputFolder) ? "Same folder as the parts" : _outputFolder;

    public bool HasCustomOutput => !string.IsNullOrEmpty(_outputFolder);

    public bool ExtractArchives
    {
        get => _extractArchives;
        set
        {
            if (SetField(ref _extractArchives, value))
            {
                _settings.ExtractArchives = value;
                Raise(nameof(KeepArchives));
                RefreshItems();
            }
        }
    }

    /// <summary>Mirror of <see cref="ExtractArchives"/> for the second segment button.</summary>
    public bool KeepArchives
    {
        get => !_extractArchives;
        set => ExtractArchives = !value;
    }

    public bool RecycleParts
    {
        get => _recycleParts;
        set { if (SetField(ref _recycleParts, value)) _settings.RecycleParts = value; }
    }

    public bool OpenFolderWhenDone
    {
        get => _openFolderWhenDone;
        set { if (SetField(ref _openFolderWhenDone, value)) _settings.OpenFolderWhenDone = value; }
    }

    // ----- Updates -----

    private UpdateInfo? _update;

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates == value) return;
            _settings.CheckForUpdates = value;
            Raise(nameof(CheckForUpdates));
            if (value)
                _ = CheckForUpdatesAsync(announce: true);
            else
                SetUpdate(null);
        }
    }

    public bool UpdateAvailable => _update is not null;

    public UpdateInfo? Update => _update;

    public string UpdateText => _update is null ? "" : $"Update to {_update.Version.ToString(3)}";

    public string UpdateTooltip => _update is null ? "" :
        $"Part Combiner {_update.Version.ToString(3)} is available (you have {UpdateChecker.CurrentVersion.ToString(3)}).\nClick to update.";

    /// <summary>Asks GitHub for a newer release. Quiet unless <paramref name="announce"/> or an update turns up.</summary>
    public async Task CheckForUpdatesAsync(bool announce = false)
    {
        if (!_settings.CheckForUpdates) return;
        var update = await UpdateChecker.CheckAsync(UpdateChecker.CurrentVersion, CancellationToken.None);
        if (!_settings.CheckForUpdates) return;
        SetUpdate(update);
        if (announce && !IsBusy)
            SetStatus(update is null ? "You have the latest version" : $"Part Combiner {update.Version.ToString(3)} is available");
    }

    private void SetUpdate(UpdateInfo? update)
    {
        _update = update;
        Raise(nameof(UpdateAvailable), nameof(Update), nameof(UpdateText), nameof(UpdateTooltip));
    }

    /// <summary>
    /// The published app is one self-contained exe, so updating is: download the new exe, check it, swap it in.
    /// Not possible for development builds (exe + DLLs) or a folder this user can't write to.
    /// </summary>
    public static bool CanSelfUpdate(out string exePath)
    {
        exePath = Environment.ProcessPath ?? "";
        // Assembly.Location is empty exactly when running as a single-file bundle, which is what we want to know.
#pragma warning disable IL3000
        bool singleFile = string.IsNullOrEmpty(typeof(MainViewModel).Assembly.Location);
#pragma warning restore IL3000
        if (!singleFile || exePath.Length == 0) return false;
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exePath)!, $".pc-write-test-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Downloads and swaps in the update. Returns the exe to restart into, or null if it failed (status says why).</summary>
    public async Task<string?> DownloadUpdateAsync()
    {
        if (_update is null || IsBusy || !CanSelfUpdate(out var exe)) return null;

        IsBusy = true;
        OverallProgress = 0;
        SetStatus($"Downloading Part Combiner {_update.Version.ToString(3)}…");
        var download = exe + ".download";
        try
        {
            var progress = new Progress<double>(f =>
            {
                OverallProgress = f;
                SetStatus($"Downloading Part Combiner {_update.Version.ToString(3)}…  {f:P0}");
            });
            var error = await UpdateChecker.DownloadAsync(_update, download, progress, CancellationToken.None);
            if (error is not null)
            {
                SetStatus(error, error: true);
                return null;
            }
            UpdateChecker.Swap(exe, download);
            SetStatus("Update installed — restarting…");
            return exe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Couldn't install the update: " + ex.Message, error: true);
            try { if (File.Exists(download)) File.Delete(download); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
        finally
        {
            IsBusy = false;
            OverallProgress = 0;
        }
    }

    /// <summary>The "Combine with Part Combiner" item in Explorer's right-click menu (stored in the registry, not settings).</summary>
    public bool ShowInExplorerMenu
    {
        get
        {
            try { return ShellMenu.Current.IsEnabled; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
        set
        {
            try
            {
                if (value && Environment.ProcessPath is { } exe)
                {
                    ShellMenu.Current.Enable(exe);
                    if (!IsBusy) SetStatus("Added to the Explorer right-click menu (on Windows 11: Show more options)");
                }
                else if (!value)
                {
                    ShellMenu.Current.Disable();
                    if (!IsBusy) SetStatus("Removed from the Explorer right-click menu");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (!IsBusy) SetStatus("Couldn't change the Explorer menu: " + ex.Message, error: true);
            }
            Raise(nameof(ShowInExplorerMenu));
        }
    }

    /// <summary>Set from the PasswordBox in code-behind (PasswordBox can't be data-bound).</summary>
    public string Password { get; set; } = "";

    // ----- State -----

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                Raise(nameof(CanCombine), nameof(IsIdle), nameof(TaskbarState), nameof(CanSwitchMode));
        }
    }

    public bool IsIdle => !_isBusy;

    public bool IsScanning
    {
        get => _isScanning;
        private set { if (SetField(ref _isScanning, value)) Raise(nameof(CanCombine)); }
    }

    public bool IsEmpty => Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    public string ListHeader => Items.Count == 1 ? "1 set" : $"{Items.Count} sets";

    public int CombineCount => Items.Count(i => i.CanProcess);

    public bool CanCombine => !_isBusy && !_isScanning && CombineCount > 0;

    public string CombineText => CombineCount > 1 ? $"Combine {CombineCount}" : "Combine";

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    public bool StatusIsError { get => _statusIsError; private set => SetField(ref _statusIsError, value); }

    public double OverallProgress { get => _overallProgress; private set => SetField(ref _overallProgress, value); }

    public string SpeedText { get => _speedText; private set => SetField(ref _speedText, value); }

    public TaskbarItemProgressState TaskbarState => _isBusy ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;

    /// <summary>Raised when a run finished with a password problem, so the view can focus the password box.</summary>
    public event EventHandler? PasswordNeeded;

    // ----- Actions -----

    /// <summary>
    /// Adds paths after a short pause, merging everything that arrives meanwhile into one scan. Explorer starts
    /// one process per selected file, so five selected parts arrive as five separate hand-overs.
    /// </summary>
    public void QueuePaths(IEnumerable<string> paths)
    {
        _queuedPaths.AddRange(paths);
        _queueTimer.Stop();
        _queueTimer.Start();
    }

    private async void FlushQueuedPaths()
    {
        _queueTimer.Stop();
        if (_queuedPaths.Count == 0) return;
        var paths = _queuedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _queuedPaths.Clear();
        await AddPathsAsync(paths);
    }

    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        // One scan at a time, so results and the status line don't step on each other.
        await _scanLock.WaitAsync();
        try
        {
            await AddPathsCoreAsync(paths);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task AddPathsCoreAsync(IReadOnlyList<string> paths)
    {
        IsScanning = true;
        SetStatus("Looking for parts…");
        DetectionResult result;
        try
        {
            result = await Task.Run(() => PartDetector.Detect(paths));
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't read those files: " + ex.Message, error: true);
            return;
        }
        finally
        {
            IsScanning = false;
        }

        int added = 0, updated = 0;
        foreach (var set in result.Sets)
        {
            // Sets that can't be combined open up so the user sees exactly what's wrong.
            var vm = new PartSetViewModel(set, _extractArchives, EngineAvailable) { IsExpanded = set.IsBlocked };
            int index = IndexOf(set.Key);
            if (index < 0)
            {
                Items.Add(vm);
                added++;
            }
            else if (!Items[index].IsWorking)
            {
                Items[index] = vm;
                updated++;
            }
        }
        RaiseCounts();

        if (IsBusy) return;
        if (result.Sets.Count == 0)
        {
            var n = result.UnrecognizedFiles.Count;
            SetStatus(n == 1
                ? $"{Path.GetFileName(result.UnrecognizedFiles[0])} isn't part of a split set"
                : n > 1 ? $"None of those {n} files are split parts" : "No split parts found there", error: true);
        }
        else
        {
            var parts = new List<string>();
            if (added > 0) parts.Add(added == 1 ? "Found 1 set" : $"Found {added} sets");
            if (updated > 0) parts.Add(updated == 1 ? "refreshed 1" : $"refreshed {updated}");
            if (result.UnrecognizedFiles.Count > 0) parts.Add($"skipped {result.UnrecognizedFiles.Count} other file(s)");
            var text = string.Join(", ", parts);
            text = text.Length > 0 ? char.ToUpper(text[0]) + text[1..] : DefaultStatus();

            int blocked = result.Sets.Count(s => s.IsBlocked);
            if (blocked > 0)
            {
                text += blocked == 1
                    ? "  —  1 set won't be combined: parts are missing or don't match"
                    : $"  —  {blocked} sets won't be combined: parts are missing or don't match";
            }
            SetStatus(text, error: blocked > 0);
        }
    }

    public void Clear()
    {
        foreach (var item in Items.Where(i => i.CanRemove).ToList())
            Items.Remove(item);
        if (!IsBusy) SetStatus(DefaultStatus());
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>The combine run in progress, if any (the window waits for it before closing).</summary>
    public Task? CurrentRun { get; private set; }

    public Task CombineAsync()
    {
        if (IsBusy) return CurrentRun ?? Task.CompletedTask;
        CurrentRun = RunAsync();
        return CurrentRun;
    }

    private async Task RunAsync()
    {
        var queue = Items.Where(i => i.CanProcess).ToList();
        if (queue.Count == 0) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var service = new CombineService(_engine);
        var options = new CombineOptions
        {
            OutputDirectory = string.IsNullOrWhiteSpace(_outputFolder) ? null : _outputFolder,
            ExtractArchives = _extractArchives,
            Password = string.IsNullOrEmpty(Password) ? null : Password,
            RecycleParts = _recycleParts,
        };

        _runTotalBytes = Math.Max(1, queue.Sum(i => i.Set.TotalSize));
        _runProcessedBytes = 0;
        _lastSampleBytes = 0;
        _lastSampleTime = DateTime.UtcNow;
        _bytesPerSecond = 0;
        OverallProgress = 0;
        SpeedText = "";
        _speedTimer.Start();

        int done = 0, failed = 0;
        bool canceled = false, passwordProblem = false;
        string? lastOutput = null;
        long finishedBytes = 0;

        for (int n = 0; n < queue.Count; n++)
        {
            var item = queue[n];
            if (token.IsCancellationRequested) { canceled = true; break; }

            item.Begin();
            SetStatus(queue.Count > 1 ? $"Combining {n + 1} of {queue.Count}  ·  {item.Title}" : $"Combining {item.Title}");
            long size = item.Set.TotalSize;
            long baseBytes = finishedBytes;
            var progress = new Progress<CombineProgress>(p =>
            {
                item.Report(p);
                _runProcessedBytes = baseBytes + (long)(p.Fraction * size);
                OverallProgress = (double)_runProcessedBytes / _runTotalBytes;
            });

            try
            {
                var result = await service.CombineAsync(item.Set, options, progress, token);
                item.Complete(result);
                if (result.Success)
                {
                    done++;
                    lastOutput = result.OutputPath ?? lastOutput;
                }
                else
                {
                    failed++;
                    passwordProblem |= result.PasswordProblem;
                }
            }
            catch (OperationCanceledException)
            {
                item.MarkCanceled();
                canceled = true;
                break;
            }
            catch (Exception ex)
            {
                item.Fail(ex.Message);
                failed++;
            }

            finishedBytes += size;
            _runProcessedBytes = finishedBytes;
            OverallProgress = (double)finishedBytes / _runTotalBytes;
        }

        _speedTimer.Stop();
        SpeedText = "";
        _cts.Dispose();
        _cts = null;
        IsBusy = false;
        RaiseCounts();

        var summary = new List<string>();
        if (done > 0) summary.Add(done == 1 ? "1 set combined" : $"{done} sets combined");
        if (failed > 0) summary.Add($"{failed} failed");
        if (canceled) summary.Add("canceled");
        int blocked = Items.Count(i => i.State == ItemState.Blocked);
        if (blocked > 0) summary.Add($"{blocked} skipped because of problems");
        var status = summary.Count > 0 ? string.Join("  ·  ", summary) : "Nothing to do";
        if (passwordProblem) status += "  —  enter the password and press Combine to retry";
        SetStatus(char.ToUpper(status[0]) + status[1..], error: failed > 0 || passwordProblem || blocked > 0);

        if (passwordProblem)
            PasswordNeeded?.Invoke(this, EventArgs.Empty);

        if (done > 0 && !canceled && _openFolderWhenDone && lastOutput is not null)
            Reveal(lastOutput);
    }

    // ----- Helpers -----

    private void UpdateSpeed()
    {
        var now = DateTime.UtcNow;
        double seconds = (now - _lastSampleTime).TotalSeconds;
        if (seconds <= 0) return;

        double instant = Math.Max(0, _runProcessedBytes - _lastSampleBytes) / seconds;
        _bytesPerSecond = _bytesPerSecond <= 0 ? instant : _bytesPerSecond * 0.7 + instant * 0.3;
        _lastSampleBytes = _runProcessedBytes;
        _lastSampleTime = now;

        if (_bytesPerSecond < 1)
        {
            SpeedText = "";
            return;
        }
        double remaining = Math.Max(0, _runTotalBytes - _runProcessedBytes) / _bytesPerSecond;
        SpeedText = $"{Bytes.Format((long)_bytesPerSecond)}/s  ·  {FormatDuration(remaining)} left";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{Math.Max(1, Math.Ceiling(seconds)):0} s";
        if (seconds < 3600) return $"{Math.Ceiling(seconds / 60):0} min";
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours} h {t.Minutes} min";
    }

    private void RefreshItems()
    {
        foreach (var item in Items)
            item.Refresh(_extractArchives, EngineAvailable);
        RaiseCounts();
    }

    private void RaiseCounts() =>
        Raise(nameof(IsEmpty), nameof(HasItems), nameof(ListHeader), nameof(CombineCount), nameof(CanCombine), nameof(CombineText));

    private int IndexOf(string key)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Key == key) return i;
        return -1;
    }

    private string DefaultStatus() => Items.Count == 0
        ? "Add split parts to get started"
        : CombineCount == 0 ? "Nothing ready to combine" : "Ready";

    private void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }

    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
