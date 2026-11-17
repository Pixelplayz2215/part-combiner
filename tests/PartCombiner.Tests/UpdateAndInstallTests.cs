using System.Security.Cryptography;
using Microsoft.Win32;
using PartCombiner.Core;

namespace PartCombiner.Tests;

public class UpdateCheckerTests
{
    private const string Release = """
        {
          "tag_name": "v1.2.0", "name": "Part Combiner 1.2.0", "draft": false, "prerelease": false,
          "html_url": "https://github.com/Pixelplayz2215/part-combiner/releases/tag/v1.2.0",
          "body": "What's new",
          "assets": [
            { "name": "PartCombinerSetup.exe", "size": 10, "browser_download_url": "https://example.invalid/setup.exe",
              "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111" },
            { "name": "PartCombiner.exe", "size": 65021796,
              "browser_download_url": "https://github.com/Pixelplayz2215/part-combiner/releases/download/v1.2.0/PartCombiner.exe",
              "digest": "sha256:B0C109B2B0D0953ECC32130BB5B34DB5C7C260C3C022737833A6DAAF08E1CB70" }
          ]
        }
        """;

    [Fact]
    public void ParsesTheReleaseAndPicksThePortableExe()
    {
        var info = UpdateChecker.ParseRelease(Release)!;
        Assert.Equal(new Version(1, 2, 0), info.Version);
        Assert.EndsWith("/v1.2.0/PartCombiner.exe", info.DownloadUrl);
        Assert.Equal(65021796, info.Size);
        Assert.Equal("b0c109b2b0d0953ecc32130bb5b34db5c7c260c3c022737833a6daaf08e1cb70", info.Sha256);
        Assert.Equal("What's new", info.Notes);
    }

    [Fact]
    public void IgnoresDraftsAndPreReleases()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateChecker.ParseRelease(Release.Replace("\"draft\": false", "\"draft\": true")));
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2.0.1", "2.0.1")]
    [InlineData("v1.3.0-beta.1", "1.3.0")]
    public void ParsesTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    public void RejectsTagsThatArentVersions(string tag) => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public async Task Download_WithoutChecksum_IsRefused()
    {
        using var d = new TempDir();
        var info = new UpdateInfo(new Version(9, 0, 0), "v9.0.0", "https://example.invalid", "https://example.invalid/x.exe", 1, null, "");
        var error = await UpdateChecker.DownloadAsync(info, d["x.exe"], null, default);
        Assert.Contains("checksum", error);
        Assert.False(File.Exists(d["x.exe"]));
    }

    [Fact]
    public void Swap_ReplacesTheExe_AndKeepsTheOldOneAside()
    {
        using var d = new TempDir();
        var exe = d.File("PartCombiner.exe", [1, 1, 1]);
        var downloaded = d.File("PartCombiner.exe.download", [2, 2]);

        UpdateChecker.Swap(exe, downloaded);
        Assert.Equal([2, 2], File.ReadAllBytes(exe));
        Assert.True(File.Exists(exe + ".old"));
        Assert.False(File.Exists(downloaded));

        UpdateChecker.CleanUpAfterUpdate(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task LiveCheck_AgainstGitHub_FindsTheCurrentRelease()
    {
        // Pretending to be 0.1 must find the published release; skipped quietly when offline.
        var update = await UpdateChecker.CheckAsync(new Version(0, 1, 0), default);
        if (update is null) return;
        Assert.True(update.Version >= new Version(1, 1, 0));
        Assert.NotNull(update.Sha256);
        Assert.Null(await UpdateChecker.CheckAsync(new Version(999, 0, 0), default));
    }

    [Fact]
    public async Task LiveDownload_IsVerifiedAgainstGitHubsChecksum()
    {
        var update = await UpdateChecker.CheckAsync(new Version(0, 1, 0), default);
        if (update is null) return; // offline
        using var d = new TempDir();

        // Real download, checked against the real digest.
        Assert.Null(await UpdateChecker.DownloadAsync(update, d["ok.exe"], null, default));
        Assert.Equal(update.Size, new FileInfo(d["ok.exe"]).Length);

        // Same file, wrong expected digest: rejected and removed.
        var tampered = update with { Sha256 = Convert.ToHexStringLower(SHA256.HashData("nope"u8)) };
        var error = await UpdateChecker.DownloadAsync(tampered, d["bad.exe"], null, default);
        Assert.Contains("doesn't match", error);
        Assert.False(File.Exists(d["bad.exe"]));
    }
}

/// <summary>Install/uninstall against scratch folders and a scratch registry key — never the real Start menu.</summary>
public sealed class InstallerTests : IDisposable
{
    private readonly TempDir _d = new();
    private readonly string _regRoot = @"Software\PartCombinerTests\" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\PartCombinerTests", throwOnMissingSubKey: false);
        _d.Dispose();
    }

    private Installer NewInstaller(out ShellMenu menu)
    {
        menu = new ShellMenu(_regRoot + @"\Classes");
        return new Installer(_d[Path.Combine("Programs", "Part Combiner")], _d["StartMenu"], _d["Desktop"], _regRoot + @"\Uninstall", menu);
    }

    [Fact]
    public void InstallThenUninstall_LeavesNothingBehind()
    {
        var installer = NewInstaller(out var menu);
        var source = _d.File(Path.Combine("download", "PartCombinerSetup.exe"), 5000, 0x4D);
        var settings = _d.File(Path.Combine("AppData", "PartCombiner", "settings.json"), 10);

        Assert.Null(installer.FindInstalled());
        installer.Install(source, new Version(1, 2, 0), new InstallOptions(StartMenuShortcut: true, DesktopShortcut: true, ExplorerMenu: true));

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(installer.InstalledExe));
        Assert.Equal(new Version(1, 2, 0), installer.FindInstalled()!.Version);
        Assert.Equal(installer.InstalledExe, Installer.ReadShortcutTarget(Path.Combine(_d["StartMenu"], "Part Combiner.lnk")), ignoreCase: true);
        Assert.True(installer.HasDesktopShortcut);
        Assert.Equal(installer.InstalledExe, menu.RegisteredExe);
        using (var key = Registry.CurrentUser.OpenSubKey(_regRoot + @"\Uninstall\PartCombiner")!)
            Assert.Equal($"\"{installer.InstalledExe}\" --uninstall", key.GetValue("UninstallString"));

        installer.Uninstall(Path.GetDirectoryName(settings));
        Assert.Null(installer.FindInstalled());
        Assert.False(File.Exists(Path.Combine(_d["StartMenu"], "Part Combiner.lnk")));
        Assert.False(installer.HasDesktopShortcut);
        Assert.False(menu.IsEnabled);
        Assert.False(Directory.Exists(Path.GetDirectoryName(settings)));
    }

    [Fact]
    public void InstallingAgain_UpdatesInPlace_EvenWhileTheOldExeIsInUse()
    {
        var installer = NewInstaller(out _);
        installer.Install(_d.File("v1.exe", 100, 1), new Version(1, 1, 0), new InstallOptions(true, true, false));

        using (new FileStream(installer.InstalledExe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            // Like a running exe: can be renamed, not overwritten.
            installer.Install(_d.File("v2.exe", 200, 2), new Version(1, 2, 0), new InstallOptions(true, false, false));
        }
        Assert.Equal(200, new FileInfo(installer.InstalledExe).Length);
        Assert.Equal(new Version(1, 2, 0), installer.FindInstalled()!.Version);
        Assert.False(installer.HasDesktopShortcut); // turned off this time
    }

    [Fact]
    public void ExplorerMenuOfAPortableCopy_IsLeftAlone()
    {
        var installer = NewInstaller(out var menu);
        var portable = _d.File(Path.Combine("Tools", "PartCombiner.exe"), 10);
        menu.Enable(portable);

        installer.Install(_d.File("setup.exe", 10), new Version(1, 2, 0), new InstallOptions(true, false, ExplorerMenu: false));
        installer.Uninstall(null);
        Assert.Equal(portable, menu.RegisteredExe);
    }

    [Fact]
    public void RefreshVersion_OnlyTouchesTheInstalledCopy()
    {
        var installer = NewInstaller(out _);
        installer.Install(_d.File("setup.exe", 10), new Version(1, 1, 0), new InstallOptions(false, false, false));

        installer.RefreshVersion(_d.File("elsewhere.exe", 10), new Version(5, 0, 0));
        Assert.Equal(new Version(1, 1, 0), installer.FindInstalled()!.Version);

        installer.RefreshVersion(installer.InstalledExe, new Version(1, 2, 0));
        Assert.Equal(new Version(1, 2, 0), installer.FindInstalled()!.Version);
    }
}
