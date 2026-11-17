using System.Windows;

namespace PartCombiner;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        var settings = AppSettings.Load();
        if (Environment.ProcessPath is { } exe)
            Core.ShellMenu.Current.RepairIfMoved(exe);
        var window = new MainWindow(settings, e.Args);
        MainWindow = window;
        window.Show();

        _instance?.Listen(paths => Dispatcher.BeginInvoke(() => window.ReceiveFromOtherLaunch(paths)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
