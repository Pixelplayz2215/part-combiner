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
    public void Enable_WritesFileAndFolderVerbs_AndDisableRemovesThem()
    {
        var menu = new ShellMenu(_root);
        Assert.False(menu.IsEnabled);

        const string exe = @"C:\Tools\Part Combiner\PartCombiner.exe";
        menu.Enable(exe);
        Assert.True(menu.IsEnabled);
        Assert.Equal(exe, menu.RegisteredExe);

        using (var file = Registry.CurrentUser.OpenSubKey(_root + @"\*\shell\PartCombiner.Combine")!)
        {
            Assert.Equal(ShellMenu.MenuText, file.GetValue("MUIVerb"));
            Assert.Equal(ShellMenu.AppliesTo, file.GetValue("AppliesTo"));
        }
        using (var command = Registry.CurrentUser.OpenSubKey(_root + @"\Directory\shell\PartCombiner.Combine\command")!)
            Assert.Equal($"\"{exe}\" \"%1\"", command.GetValue(null));

        menu.Disable();
        Assert.False(menu.IsEnabled);
        Assert.Null(Registry.CurrentUser.OpenSubKey(_root + @"\Directory\shell\PartCombiner.Combine"));
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
