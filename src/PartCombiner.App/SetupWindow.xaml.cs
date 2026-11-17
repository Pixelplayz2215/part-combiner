using System.Diagnostics;
using System.IO;
using System.Windows;
using PartCombiner.Core;

namespace PartCombiner;

public enum SetupMode { Install, Uninstall }

/// <summary>The installer and uninstaller: the same exe started as "PartCombinerSetup.exe" / --install, or --uninstall.</summary>
public partial class SetupWindow : Window
{
    private readonly SetupViewModel _vm;

    public SetupWindow(SetupMode mode)
    {
        InitializeComponent();
        _vm = new SetupViewModel(mode, Installer.Default);
        DataContext = _vm;
        SourceInitialized += (_, _) => ThemeManager.StyleTitleBar(this);
    }

    private async void OnAction(object sender, RoutedEventArgs e) => await _vm.RunAsync();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        if (_vm.IsInstall && _vm.LaunchWhenDone)
        {
            try { Process.Start(new ProcessStartInfo(_vm.InstalledExe) { UseShellExecute = false, WorkingDirectory = _vm.InstallDir }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsWorking) e.Cancel = true; // don't stop halfway
        base.OnClosing(e);
    }
}

public sealed class SetupViewModel : ObservableObject
{
    private enum Page { Ready, Working, Done }

    private readonly SetupMode _mode;
    private readonly Installer _installer;
    private readonly InstalledApp? _existing;
    private Page _page = Page.Ready;
    private string _errorText = "";
    private bool _startMenu = true, _desktop, _explorerMenu, _removeSettings, _launch = true;

    public SetupViewModel(SetupMode mode, Installer installer)
    {
        _mode = mode;
        _installer = installer;
        _existing = installer.FindInstalled();
        _desktop = installer.HasDesktopShortcut;
        try { _explorerMenu = ShellMenu.Current.IsEnabled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static string Version => UpdateChecker.CurrentVersion.ToString(3);

    public bool IsInstall => _mode == SetupMode.Install;
    public bool IsUninstall => _mode == SetupMode.Uninstall;
    public bool IsReady => _page == Page.Ready;
    public bool IsWorking => _page == Page.Working;
    public bool IsDone => _page == Page.Done;
    public bool ShowInstallButton => IsReady && IsInstall;
    public bool ShowUninstallButton => IsReady && IsUninstall;

    public string InstallDir => _installer.InstallDir;
    public string InstalledExe => _installer.InstalledExe;

    public string WindowTitle => IsInstall ? "Part Combiner Setup" : "Uninstall Part Combiner";

    public string Subtitle => IsInstall ? $"Setup  ·  version {Version}" : $"Version {_existing?.Version?.ToString(3) ?? Version}";

    private string InstallKind
    {
        get
        {
            if (_existing?.Version is not { } installed) return "install";
            var current = UpdateChecker.CurrentVersion;
            int cmp = new Version(current.Major, current.Minor, Math.Max(0, current.Build))
                .CompareTo(new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build)));
            return cmp > 0 ? "update" : cmp == 0 ? "reinstall" : "downgrade";
        }
    }

    public string Heading => IsUninstall ? "Uninstall Part Combiner?" : InstallKind switch
    {
        "update" => $"Update Part Combiner to {Version}",
        "reinstall" => "Reinstall Part Combiner",
        "downgrade" => $"Install the older version {Version}",
        _ => "Install Part Combiner",
    };

    public string Description => IsUninstall
        ? "This removes the program, its shortcuts and its entry in Settings › Apps. Files you combined aren't touched."
        : InstallKind switch
        {
            "update" => $"Version {_existing!.Version!.ToString(3)} is installed. Your settings are kept.",
            "reinstall" => "This version is already installed. Reinstalling repairs the program and its shortcuts.",
            "downgrade" => $"A newer version ({_existing!.Version!.ToString(3)}) is installed. This replaces it with {Version}.",
            _ => "Rejoins split archives and files: drop in any part and it finds, checks and combines the rest.",
        };

    public string ActionText => InstallKind switch { "update" => "Update", "reinstall" => "Reinstall", _ => "Install" };

    public string WorkingText => IsInstall ? "Installing…" : "Removing…";

    public string DoneHeading => IsInstall
        ? (InstallKind == "update" ? "Part Combiner is up to date" : "Part Combiner is installed")
        : "Part Combiner was removed";

    public string DoneText => IsInstall
        ? (StartMenuShortcut ? "You'll find it in the Start menu." : "It's ready to use.")
        : "Thanks for using it.";

    public string FinishText => IsInstall ? "Finish" : "Close";

    public bool StartMenuShortcut { get => _startMenu; set => SetField(ref _startMenu, value); }
    public bool DesktopShortcut { get => _desktop; set => SetField(ref _desktop, value); }
    public bool ExplorerMenu { get => _explorerMenu; set => SetField(ref _explorerMenu, value); }
    public bool RemoveSettings { get => _removeSettings; set => SetField(ref _removeSettings, value); }
    public bool LaunchWhenDone { get => _launch; set => SetField(ref _launch, value); }

    public string ErrorText { get => _errorText; private set { SetField(ref _errorText, value); Raise(nameof(HasError)); } }
    public bool HasError => _errorText.Length > 0;

    public async Task RunAsync()
    {
        if (!IsReady) return;
        ErrorText = "";

        var running = OtherCopiesRunningFromInstallDir();
        if (running > 0 && IsUninstall)
        {
            ErrorText = "Part Combiner is still open. Close it, then try again.";
            return;
        }

        SetPage(Page.Working);
        var started = DateTime.UtcNow;
        try
        {
            var source = Environment.ProcessPath ?? throw new IOException("Can't tell where this program is");
            var options = new InstallOptions(_startMenu, _desktop, _explorerMenu);
            if (IsInstall)
                await Task.Run(() => _installer.Install(source, UpdateChecker.CurrentVersion, options));
            else
                await Task.Run(() => _installer.Uninstall(_removeSettings ? AppSettings.Folder : null));

            // A short pause so the step is visible rather than a flicker.
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < TimeSpan.FromMilliseconds(700))
                await Task.Delay(TimeSpan.FromMilliseconds(700) - elapsed);

            if (IsUninstall)
                _installer.ScheduleFolderRemoval();
            SetPage(Page.Done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or System.Runtime.InteropServices.COMException)
        {
            SetPage(Page.Ready);
            ErrorText = (IsInstall ? "Installing failed: " : "Uninstalling failed: ") + ex.Message;
        }
    }

    private void SetPage(Page page)
    {
        _page = page;
        Raise(nameof(IsReady), nameof(IsWorking), nameof(IsDone), nameof(ShowInstallButton), nameof(ShowUninstallButton),
              nameof(DoneText), nameof(DoneHeading));
    }

    /// <summary>Copies of the app running from the install folder, other than this process.</summary>
    private int OtherCopiesRunningFromInstallDir()
    {
        int count = 0;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Installer.ExeName)))
        {
            using (p)
            {
                try
                {
                    if (p.Id != Environment.ProcessId && p.MainModule?.FileName is { } file && _installer.IsInstalledCopy(file))
                        count++;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return count;
    }
}
