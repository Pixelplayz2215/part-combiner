using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PartCombiner.Core;

/// <summary>
/// The optional "Combine with Part Combiner" item in Explorer's right-click menu, for part files and folders.
/// It's written per user under HKCU\Software\Classes, so no admin rights are needed. On Windows 11 it appears
/// under "Show more options".
/// </summary>
public sealed class ShellMenu
{
    public const string MenuText = "Combine with Part Combiner";
    private const string VerbName = "PartCombiner.Combine";

    /// <summary>
    /// Which files show the item, as a Windows Search (AQS) filter on the last extension. Covers .001 … .999 and
    /// longer numbers, .part1 …, .r00 …, .s00 …, .z01 …, .rar, .aa/.aaa and .crc. It can't be exact (.3gp also
    /// starts with a digit), so the few common look-alikes are excluded; anything else just reports "not a split set".
    /// </summary>
    internal static readonly string AppliesTo = BuildAppliesTo();

    private readonly string _classesRoot;

    /// <param name="classesRoot">Key under HKEY_CURRENT_USER. Tests point this somewhere harmless.</param>
    internal ShellMenu(string classesRoot) => _classesRoot = classesRoot;

    public static ShellMenu Current { get; } = new(@"Software\Classes");

    private string FileVerb => $@"{_classesRoot}\*\shell\{VerbName}";
    private string FolderVerb => $@"{_classesRoot}\Directory\shell\{VerbName}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(FileVerb);
            return key is not null;
        }
    }

    /// <summary>The exe the menu item currently starts, or null when it isn't set up.</summary>
    public string? RegisteredExe
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(FileVerb + @"\command");
            return key?.GetValue(null) is string command && command.StartsWith('"')
                ? command[1..Math.Max(1, command.IndexOf('"', 1))]
                : null;
        }
    }

    public void Enable(string exePath)
    {
        Write(FileVerb, exePath, AppliesTo);
        Write(FolderVerb, exePath, appliesTo: null);
        NotifyExplorer();
    }

    public void Disable()
    {
        Registry.CurrentUser.DeleteSubKeyTree(FileVerb, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(FolderVerb, throwOnMissingSubKey: false);
        NotifyExplorer();
    }

    /// <summary>If the item points at an exe that no longer exists (the app was moved), point it at this one.</summary>
    public void RepairIfMoved(string exePath)
    {
        try
        {
            if (IsEnabled && RegisteredExe is { } registered && !File.Exists(registered) && File.Exists(exePath))
                Enable(exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static void Write(string path, string exePath, string? appliesTo)
    {
        using var verb = Registry.CurrentUser.CreateSubKey(path);
        verb.SetValue("MUIVerb", MenuText);
        verb.SetValue("Icon", $"\"{exePath}\",0");
        if (appliesTo is not null)
            verb.SetValue("AppliesTo", appliesTo);
        using var command = verb.CreateSubKey("command");
        command.SetValue(null, $"\"{exePath}\" \"%1\"");
    }

    private static string BuildAppliesTo()
    {
        static string Like(string pattern) => $"System.FileExtension:~\"{pattern}\"";
        static string Is(string ext) => $"System.FileExtension:=\"{ext}\"";

        var include = new List<string>();
        for (char c = '0'; c <= '9'; c++) include.Add(Like($".{c}*"));           // .001, .7z.001 → .001, .0001 …
        foreach (char letter in "rsz")
            for (char c = '0'; c <= '9'; c++) include.Add(Like($".{letter}{c}?")); // .r00, .s01, .z01 …
        include.Add(Like(".part*"));                                              // .part1, .zip.part01 …
        include.AddRange([Is(".rar"), Is(".aa"), Is(".aaa"), Is(".crc")]);

        string[] exclude = [".3gp", ".3g2", ".7z"];
        return "(" + string.Join(" OR ", include) + ")" + string.Concat(exclude.Select(e => " AND NOT " + Is(e)));
    }

    private static void NotifyExplorer()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
