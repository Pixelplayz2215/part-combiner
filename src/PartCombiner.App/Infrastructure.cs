using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PartCombiner;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>User preferences, stored as JSON in %APPDATA%\PartCombiner.</summary>
public sealed class AppSettings
{
    public string? OutputFolder { get; set; }
    public bool ExtractArchives { get; set; } = true;
    public bool RecycleParts { get; set; }
    public bool OpenFolderWhenDone { get; set; } = true;
    public string? SevenZipPath { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public double WindowWidth { get; set; } = 760;
    public double WindowHeight { get; set; } = 820;
    public SavedPlacement? Placement { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PartCombiner");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Follows the Windows light/dark app setting and paints the native title bar to match.</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static void Initialize(Application app)
    {
        Apply(app, ReadSystemIsDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
                return;
            app.Dispatcher.BeginInvoke(() =>
            {
                bool dark = ReadSystemIsDark();
                if (dark != IsDark) Apply(app, dark);
            });
        };
    }

    private static void Apply(Application app, bool dark)
    {
        IsDark = dark;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PartCombiner;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        var merged = app.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;

        foreach (Window w in app.Windows)
            StyleTitleBar(w);
    }

    public static void StyleTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || _current is null) return;

        int dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (_current["Color.Bg"] is Color bg)
        {
            int caption = ToColorRef(bg);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }
        if (_current["Color.Text"] is Color text)
        {
            int textRef = ToColorRef(text);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textRef, sizeof(int));
        }
        if (_current["Color.Border"] is Color border)
        {
            int borderRef = ToColorRef(border);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderRef, sizeof(int));
        }
    }

    private static bool ReadSystemIsDark()
    {
        // PARTCOMBINER_THEME=light|dark forces a theme (handy for screenshots and testing).
        var forced = Environment.GetEnvironmentVariable("PARTCOMBINER_THEME");
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
