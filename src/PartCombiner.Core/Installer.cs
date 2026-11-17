using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace PartCombiner.Core;

public sealed record InstallOptions(bool StartMenuShortcut, bool DesktopShortcut, bool ExplorerMenu);

/// <summary>What Settings › Apps knows about an existing installation.</summary>
public sealed record InstalledApp(string Location, Version? Version);

/// <summary>
/// Per-user install: the exe goes to %LOCALAPPDATA%\Programs\Part Combiner, with Start menu / desktop shortcuts and an
/// entry in Settings › Apps (HKCU), so no admin rights are needed and the app can update itself in place.
/// </summary>
public sealed class Installer
{
    public const string AppName = "Part Combiner";
    public const string Publisher = "Pixelplayz2215";
    public const string ExeName = "PartCombiner.exe";
    private const string ShortcutName = AppName + ".lnk";
    private const string EntryName = "PartCombiner";

    private readonly string _uninstallRoot;
    private readonly ShellMenu _shellMenu;

    internal Installer(string installDir, string startMenuDir, string desktopDir, string uninstallRoot, ShellMenu shellMenu)
    {
        InstallDir = installDir;
        StartMenuDir = startMenuDir;
        DesktopDir = desktopDir;
        _uninstallRoot = uninstallRoot;
        _shellMenu = shellMenu;
    }

    public static Installer Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        ShellMenu.Current);

    public string InstallDir { get; }
    public string StartMenuDir { get; }
    public string DesktopDir { get; }
    public string InstalledExe => Path.Combine(InstallDir, ExeName);
    private string EntryKey => $@"{_uninstallRoot}\{EntryName}";
    private string StartMenuShortcut => Path.Combine(StartMenuDir, ShortcutName);
    private string DesktopShortcut => Path.Combine(DesktopDir, ShortcutName);

    public bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    /// <summary>True when <paramref name="exePath"/> is the installed copy.</summary>
    public bool IsInstalledCopy(string exePath) =>
        string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public InstalledApp? FindInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EntryKey);
        if (key?.GetValue("InstallLocation") is not string location)
            return null;
        Version.TryParse(key.GetValue("DisplayVersion") as string, out var version);
        return new InstalledApp(location, version);
    }

    /// <summary>Copies <paramref name="sourceExe"/> into place and sets up shortcuts and the Settings › Apps entry.</summary>
    public void Install(string sourceExe, Version version, InstallOptions options)
    {
        Directory.CreateDirectory(InstallDir);
        if (!IsInstalledCopy(sourceExe))
        {
            var staged = InstalledExe + ".new";
            File.Copy(sourceExe, staged, overwrite: true);
            if (File.Exists(InstalledExe))
                UpdateChecker.Swap(InstalledExe, staged); // works even while the installed copy is running
            else
                File.Move(staged, InstalledExe);
            UpdateChecker.CleanUpAfterUpdate(InstalledExe);
        }

        if (options.StartMenuShortcut)
            CreateShortcut(StartMenuShortcut, InstalledExe);
        else
            TryDelete(StartMenuShortcut);
        if (options.DesktopShortcut)
            CreateShortcut(DesktopShortcut, InstalledExe);
        else
            TryDelete(DesktopShortcut);

        WriteEntry(version);

        if (options.ExplorerMenu)
            _shellMenu.Enable(InstalledExe);
        else if (PointsIntoInstallDir(_shellMenu.RegisteredExe))
            _shellMenu.Disable();
    }

    /// <summary>
    /// Removes shortcuts, the Settings › Apps entry, the Explorer menu (if it belongs to this install) and, if asked,
    /// the settings folder. The program folder itself is removed by <see cref="ScheduleFolderRemoval"/>, because the
    /// uninstaller usually runs from inside it.
    /// </summary>
    public void Uninstall(string? settingsDir)
    {
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        Registry.CurrentUser.DeleteSubKeyTree(EntryKey, throwOnMissingSubKey: false);
        if (PointsIntoInstallDir(_shellMenu.RegisteredExe))
            _shellMenu.Disable();
        if (settingsDir is not null && Directory.Exists(settingsDir))
        {
            try { Directory.Delete(settingsDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes the program folder a few seconds after this process exits.</summary>
    public void ScheduleFolderRemoval()
    {
        // Only ever our own folder under ...\Programs\Part Combiner.
        if (!Path.GetFileName(InstallDir.TrimEnd('\\')).Equals(AppName, StringComparison.OrdinalIgnoreCase))
            return;
        // Raw arguments: cmd.exe has its own quoting rules and doesn't understand .NET's \" escaping.
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{InstallDir.TrimEnd('\\')}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        System.Diagnostics.Process.Start(psi)?.Dispose();
    }

    /// <summary>After a self-update, keep the version in Settings › Apps current.</summary>
    public void RefreshVersion(string exePath, Version version)
    {
        if (!IsInstalledCopy(exePath)) return;
        using var key = Registry.CurrentUser.OpenSubKey(EntryKey, writable: true);
        key?.SetValue("DisplayVersion", version.ToString(3));
    }

    private void WriteEntry(Version version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EntryKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version.ToString(3));
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("URLInfoAbout", "https://github.com/" + UpdateChecker.Repository);
        key.SetValue("HelpLink", "https://github.com/" + UpdateChecker.Repository);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        long kb = File.Exists(InstalledExe) ? new FileInfo(InstalledExe).Length / 1024 : 0;
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
    }

    private bool PointsIntoInstallDir(string? exe) =>
        exe is not null && string.Equals(Path.GetDirectoryName(exe), InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ----- .lnk files via the shell's IShellLink -----

    internal static void CreateShortcut(string linkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetIconLocation(target, 0);
            link.SetDescription("Rejoin split archives and files");
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    internal static string? ReadShortcutTarget(string linkPath)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(linkPath, 0);
            var buffer = new char[1024];
            link.GetPath(buffer, buffer.Length, IntPtr.Zero, 0);
            return new string(buffer).TrimEnd('\0');
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
