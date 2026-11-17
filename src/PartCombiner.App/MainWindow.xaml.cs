using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace PartCombiner;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly string[] _startupPaths;
    private bool _closeRequested;

    public MainWindow(AppSettings settings, string[] startupPaths, string? splitSource = null)
    {
        InitializeComponent();
        _settings = settings;
        _startupPaths = startupPaths;
        _vm = new MainViewModel(settings);
        DataContext = _vm;

        var area = SystemParameters.WorkArea;
        Width = Math.Clamp(settings.WindowWidth, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(settings.WindowHeight, MinHeight, Math.Max(MinHeight, area.Height));

        _vm.PasswordNeeded += (_, _) =>
        {
            PasswordInput.Focus();
            PasswordInput.SelectAll();
        };

        if (settings.Placement is not null)
            WindowStartupLocation = WindowStartupLocation.Manual;

        SourceInitialized += (_, _) =>
        {
            ThemeManager.StyleTitleBar(this);
            if (_settings.Placement is { } placement)
                WindowPlacement.Apply(this, placement);
        };
        Loaded += async (_, _) =>
        {
            if (splitSource is not null && (System.IO.File.Exists(splitSource) || System.IO.Directory.Exists(splitSource)))
            {
                _vm.IsSplitMode = true;
                _vm.Split.SetSource(splitSource);
            }
            if (_startupPaths.Length > 0)
                _vm.QueuePaths(_startupPaths);

            await Task.Delay(TimeSpan.FromSeconds(3)); // let the window settle first
            await _vm.CheckForUpdatesAsync();
        };
    }

    // ----- Updates -----

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Update is not { } update) return;
        string version = update.Version.ToString(3), current = Core.UpdateChecker.CurrentVersion.ToString(3);

        if (!MainViewModel.CanSelfUpdate(out _))
        {
            if (MessageBox.Show(this, $"Part Combiner {version} is available (you have {current}).\n\nOpen the download page?",
                    "Part Combiner", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenUrl(update.PageUrl);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Part Combiner {version} is available (you have {current}).\n\n" +
            "Yes — download it now and restart\nNo — see what's new on GitHub",
            "Part Combiner", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (answer == MessageBoxResult.No)
        {
            OpenUrl(update.PageUrl);
            return;
        }
        if (answer != MessageBoxResult.Yes) return;

        if (await _vm.DownloadUpdateAsync() is string exe)
        {
            ((App)Application.Current).RestartAfterClose(exe);
            Close();
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Paths from a later launch of the exe (Open with, the Explorer menu, a drop onto the exe).</summary>
    public void ReceiveFromOtherLaunch(string[] paths)
    {
        BringToFront();
        int split = Array.FindIndex(paths, a => a.Equals("--split", StringComparison.OrdinalIgnoreCase));
        if (split >= 0)
        {
            if (split + 1 < paths.Length && _vm.CanSwitchMode)
            {
                _vm.IsSplitMode = true;
                _vm.Split.SetSource(paths[split + 1]);
            }
            return;
        }
        paths = paths.Where(p => !p.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (paths.Length > 0)
        {
            if (_vm.CanSwitchMode) _vm.IsSplitMode = false; // parts handed over are for combining
            _vm.QueuePaths(paths);
        }
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            ShowWindow(hwnd, SW_RESTORE); // back to normal or maximized, whichever it was
        }
        Activate();
        // Activate alone can just flash the taskbar button; a brief Topmost reliably raises the window.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ----- Drag & drop -----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when the pointer left the window.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
            DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;
        if (_vm.IsSplitMode)
            _vm.Split.SetSource(paths[0]); // one thing at a time to split
        else
            await _vm.AddPathsAsync(paths);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (DropOverlay.Visibility == Visibility.Visible)
            DropOverlay.Visibility = Visibility.Collapsed; // a drag that ended outside the window
    }

    // ----- Adding parts -----

    private async void OnBrowseFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose split parts",
            Multiselect = true,
            Filter = "Split parts|*.rar;*.r??;*.s??;*.7z.*;*.zip;*.zip.*;*.z??;*.0??;*.1??;*.part*;*.aa;*.exe;*.crc|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _vm.AddPathsAsync(dialog.FileNames);
    }

    private async void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder that contains split parts", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await _vm.AddPathsAsync(dialog.FolderNames);
    }

    private void OnClear(object sender, RoutedEventArgs e) => _vm.Clear();

    // ----- Options -----

    private void OnChangeOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save combined files to" };
        if (_vm.HasCustomOutput) dialog.InitialDirectory = _vm.OutputFolder;
        if (dialog.ShowDialog(this) == true)
            _vm.OutputFolder = dialog.FolderName;
    }

    private void OnResetOutput(object sender, RoutedEventArgs e) => _vm.OutputFolder = "";

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordInput.Password;
        PasswordHint.Visibility = PasswordInput.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEngineClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.EngineAvailable)
        {
            var answer = MessageBox.Show(this,
                "7-Zip wasn't found on this PC. It's free and needed to extract RAR, 7z and ZIP archives.\n\n" +
                "Yes — open 7-zip.org to download it\nNo — locate 7z.exe yourself",
                "Part Combiner", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo("https://www.7-zip.org/download.html") { UseShellExecute = true });
                return;
            }
            if (answer != MessageBoxResult.No) return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Locate 7z.exe",
            Filter = "7-Zip console (7z.exe)|7z.exe|Programs|*.exe",
        };
        if (dialog.ShowDialog(this) == true && !_vm.TrySetEngine(dialog.FileName))
            MessageBox.Show(this, "That file can't be used.", "Part Combiner", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ----- Run -----

    private async void OnCombine(object sender, RoutedEventArgs e) => await _vm.CombineAsync();

    private void OnCancel(object sender, RoutedEventArgs e) => _vm.Cancel();

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsBusy || _vm.Split.IsBusy)
        {
            e.Cancel = true;
            if (_closeRequested) return;

            var what = _vm.Split.IsBusy ? "Splitting" : "Combining";
            var answer = MessageBox.Show(this, $"{what} is still running. Stop it and close?",
                "Part Combiner", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            // Wait for the run to stop so 7-Zip is shut down and temporary files are cleaned up.
            _closeRequested = true;
            _vm.Cancel();
            _vm.Split.Cancel();
            try
            {
                if (_vm.CurrentRun is not null) await _vm.CurrentRun;
                if (_vm.Split.CurrentRun is not null) await _vm.Split.CurrentRun;
            }
            catch (Exception) { /* already reported */ }
            Close();
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.Placement = WindowPlacement.Read(this) ?? _settings.Placement;
        _settings.Save();
        base.OnClosing(e);
    }
}
