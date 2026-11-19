using Microsoft.Win32;
using PartCombiner.Core;

namespace PartCombiner.Tests;

/// <summary>Registry side of the Explorer menu item, written under a scratch key so Explorer never sees it.</summary>
public sealed class ShellMenuTests : IDisposable
{
    private readonly string _root = @"Software\PartCombinerTests\" + Guid.NewGuid().ToString("N")[..8];

    // Only this test's own key: other test classes use sibling keys at the same time.
    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public void Enable_WritesASubmenuForFilesAndFolders_AndDisableRemovesIt()
    {
        var menu = new ShellMenu(_root);
        Assert.False(menu.IsEnabled);

        const string exe = @"C:\Tools\Part Combiner\PartCombiner.exe";
        menu.Enable(exe);
        Assert.True(menu.IsEnabled);
        Assert.Equal(exe, menu.RegisteredExe);

        foreach (var target in new[] { "*", "Directory" })
        {
            using var top = Registry.CurrentUser.OpenSubKey($@"{_root}\{target}\shell\PartCombiner")!;
            Assert.Equal(ShellMenu.MenuText, top.GetValue("MUIVerb"));
            Assert.Equal("", top.GetValue("SubCommands"));
            Assert.Null(top.GetValue("AppliesTo")); // the submenu itself shows on everything

            using var split = top.OpenSubKey(@"shell\2.split\command")!;
            Assert.Equal($"\"{exe}\" --split \"%1\"", split.GetValue(null));
            using var combine = top.OpenSubKey(@"shell\1.combine")!;
            using var combineCommand = combine.OpenSubKey("command")!;
            Assert.Equal($"\"{exe}\" \"%1\"", combineCommand.GetValue(null));
            // Combine is only offered on files that look like parts; folders always get it.
            Assert.Equal(target == "*" ? ShellMenu.AppliesTo : null, combine.GetValue("AppliesTo"));
        }

        menu.Disable();
        Assert.False(menu.IsEnabled);
        Assert.Null(Registry.CurrentUser.OpenSubKey(_root + @"\Directory\shell\PartCombiner"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_root + @"\*\shell\PartCombiner"));
    }

    [Fact]
    public void OldSingleItemEntry_IsUpgradedToTheSubmenu()
    {
        using var d = new TempDir();
        var exe = d.File("PartCombiner.exe", 10);
        // What versions up to 1.3 wrote.
        foreach (var target in new[] { "*", "Directory" })
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{_root}\{target}\shell\PartCombiner.Combine");
            verb.SetValue("MUIVerb", "Combine with Part Combiner");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        var menu = new ShellMenu(_root);
        Assert.True(menu.IsEnabled);
        Assert.Equal(exe, menu.RegisteredExe);

        menu.RepairIfMoved(exe);
        Assert.False(menu.HasLegacyEntry);
        Assert.Equal(exe, menu.RegisteredExe);
        using var split = Registry.CurrentUser.OpenSubKey($@"{_root}\*\shell\PartCombiner\shell\2.split\command")!;
        Assert.Equal($"\"{exe}\" --split \"%1\"", split.GetValue(null));

        menu.Disable(); // and turning it off removes old and new alike
        Assert.False(menu.IsEnabled);
    }

    [Fact]
    public void RepairIfMoved_RepointsOnlyWhenTheRegisteredExeIsGone()
    {
        using var d = new TempDir();
        var oldExe = d.File("old.exe", 10);
        var newExe = d.File("new.exe", 10);
        var menu = new ShellMenu(_root);

        menu.RepairIfMoved(newExe); // not enabled: stays off
        Assert.False(menu.IsEnabled);

        menu.Enable(oldExe);
        menu.RepairIfMoved(newExe); // old one still exists: leave it alone
        Assert.Equal(oldExe, menu.RegisteredExe);

        File.Delete(oldExe);
        menu.RepairIfMoved(newExe);
        Assert.Equal(newExe, menu.RegisteredExe);
    }

    [Fact]
    public void Filter_CoversThePartExtensions_AndIsBalanced()
    {
        foreach (var ext in new[] { ".0*", ".9*", ".r0?", ".r9?", ".s0?", ".z0?", ".part*", "=\".rar\"", "=\".crc\"", "=\".aa\"" })
            Assert.Contains(ext, ShellMenu.AppliesTo);
        Assert.Contains("AND NOT System.FileExtension:=\".3gp\"", ShellMenu.AppliesTo);
        Assert.Equal(ShellMenu.AppliesTo.Count(c => c == '('), ShellMenu.AppliesTo.Count(c => c == ')'));
    }
}
