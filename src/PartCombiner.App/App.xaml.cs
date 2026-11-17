using System.Diagnostics;
using System.IO;
using System.Windows;
using PartCombiner.Core;

namespace PartCombiner;

public partial class App : Application
{
    private SingleInstance? _instance;
    private string? _restartInto;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var exe = Environment.ProcessPath ?? "";

        // The same exe is also the installer ("PartCombinerSetup.exe" or --install) and the uninstaller (--uninstall).
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            ThemeManager.Initialize(this);
            ShowOnly(new SetupWindow(SetupMode.Uninstall));
            return;
        }
        if (e.Args.Contains("--install", StringComparer.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(exe).Contains("setup", StringComparison.OrdinalIgnoreCase))
        {
            ThemeManager.Initialize(this);
            ShowOnly(new SetupWindow(SetupMode.Install));
            return;
        }

        // PARTCOMBINER_MULTI=1 allows several windows (handy for testing).
        bool single = Environment.GetEnvironmentVariable("PARTCOMBINER_MULTI") != "1";
        if (single)
        {
            _instance = SingleInstance.TryClaim();
            if (_instance is null && SingleInstance.TrySend(e.Args))
            {
                Shutdown();
                return;
            }
            // If the running copy couldn't be reached, just open a window of our own.
        }

        ThemeManager.Initialize(this);

        if (exe.Length > 0)
        {
            UpdateChecker.CleanUpAfterUpdate(exe);
            ShellMenu.Current.RepairIfMoved(exe);
            try { Installer.Default.RefreshVersion(exe, UpdateChecker.CurrentVersion); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        var settings = AppSettings.Load();
        var window = new MainWindow(settings, e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray());
        MainWindow = window;
        window.Show();

        _instance?.Listen(paths => Dispatcher.BeginInvoke(() => window.ReceiveFromOtherLaunch(paths)));
    }

    private void ShowOnly(Window window)
    {
        MainWindow = window;
        window.Show();
    }

    /// <summary>Start <paramref name="exe"/> once this instance has closed (after a self-update).</summary>
    public void RestartAfterClose(string exe) => _restartInto = exe;

    protected override void OnExit(ExitEventArgs e)
    {
        // Let go of the single-instance lock first, or the new copy would just hand over to us and quit.
        _instance?.Dispose();
        _instance = null;
        if (_restartInto is not null)
        {
            try { Process.Start(new ProcessStartInfo(_restartInto) { UseShellExecute = false }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        base.OnExit(e);
    }
}
