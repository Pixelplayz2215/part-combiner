using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PartCombiner.Core;

/// <summary>
/// The optional "Part Combiner" submenu in Explorer's right-click menu, like 7-Zip's: "Split into parts…" on every
/// file and folder, and "Combine parts" on split parts and folders. It's plain registry entries under
/// HKCU\Software\Classes, so it works while the app is closed and needs no admin rights. On Windows 11 it appears
/// under "Show more options", next to 7-Zip.
/// </summary>
public sealed class ShellMenu
{
    public const string MenuText = "Part Combiner";
    public const string CombineText = "Combine parts";
    public const string SplitText = "Split into parts…";
    private const string MenuKey = "PartCombiner";
    private const string CombineKey = "1.combine";
    private const string SplitKey = "2.split";
    /// <summary>Before 1.4 there was a single "Combine with Part Combiner" item under this name.</summary>
    private const string LegacyVerb = "PartCombiner.Combine";

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

    private string FileMenu => $@"{_classesRoot}\*\shell\{MenuKey}";
    private string FolderMenu => $@"{_classesRoot}\Directory\shell\{MenuKey}";
    private string LegacyFile => $@"{_classesRoot}\*\shell\{LegacyVerb}";
    private string LegacyFolder => $@"{_classesRoot}\Directory\shell\{LegacyVerb}";

    public bool IsEnabled => KeyExists(FileMenu) || KeyExists(LegacyFile);

    /// <summary>True when the old single-item entry is still there and should be upgraded to the submenu.</summary>
    internal bool HasLegacyEntry => KeyExists(LegacyFile) || KeyExists(LegacyFolder);

    /// <summary>The exe the menu starts, or null when it isn't set up.</summary>
    public string? RegisteredExe =>
        ExeFrom($@"{FileMenu}\shell\{CombineKey}\command") ?? ExeFrom($@"{FileMenu}\shell\{SplitKey}\command") ?? ExeFrom(LegacyFile + @"\command");

    public void Enable(string exePath)
    {
        RemoveLegacy();
        // Files: Split on everything, Combine only on names that look like parts.
        WriteMenu(FileMenu, exePath, combineAppliesTo: AppliesTo);
        // Folders: both (combine the parts inside, or pack the folder and split it).
        WriteMenu(FolderMenu, exePath, combineAppliesTo: null);
        NotifyExplorer();
    }

    public void Disable()
    {
        Registry.CurrentUser.DeleteSubKeyTree(FileMenu, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(FolderMenu, throwOnMissingSubKey: false);
        RemoveLegacy();
        NotifyExplorer();
    }

    /// <summary>
    /// Keeps the menu working: if it points at an exe that no longer exists (the app was moved), point it at this
    /// one; and turn the old single "Combine with Part Combiner" item into the submenu.
    /// </summary>
    public void RepairIfMoved(string exePath)
    {
        try
        {
            if (!IsEnabled || RegisteredExe is not { } registered) return;
            if (!File.Exists(registered) && File.Exists(exePath))
                Enable(exePath);
            else if (HasLegacyEntry && File.Exists(registered))
                Enable(registered); // same exe, new layout
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static void WriteMenu(string path, string exePath, string? combineAppliesTo)
    {
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); // start clean
        using var menu = Registry.CurrentUser.CreateSubKey(path);
        menu.SetValue("MUIVerb", MenuText);
        menu.SetValue("Icon", $"\"{exePath}\",0");
        menu.SetValue("SubCommands", ""); // empty = the items are in the "shell" key below (a cascading menu)

        using (var combine = menu.CreateSubKey($@"shell\{CombineKey}"))
        {
            combine.SetValue("MUIVerb", CombineText);
            combine.SetValue("Icon", $"\"{exePath}\",0");
            if (combineAppliesTo is not null)
                combine.SetValue("AppliesTo", combineAppliesTo);
            using var command = combine.CreateSubKey("command");
            command.SetValue(null, $"\"{exePath}\" \"%1\"");
        }
        using (var split = menu.CreateSubKey($@"shell\{SplitKey}"))
        {
            split.SetValue("MUIVerb", SplitText);
            using var command = split.CreateSubKey("command");
            command.SetValue(null, $"\"{exePath}\" --split \"%1\"");
        }
    }

    private void RemoveLegacy()
    {
        Registry.CurrentUser.DeleteSubKeyTree(LegacyFile, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(LegacyFolder, throwOnMissingSubKey: false);
    }

    private static bool KeyExists(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null;
    }

    private static string? ExeFrom(string commandKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(commandKey);
        return key?.GetValue(null) is string command && command.StartsWith('"')
            ? command[1..Math.Max(1, command.IndexOf('"', 1))]
            : null;
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
