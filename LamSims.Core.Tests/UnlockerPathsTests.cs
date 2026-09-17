using System;
using System.IO;
using Xunit;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

// Shares SpecialFolderRoots with AppPathsTests, which mutates the XDG variables the parameterless
// UnlockerPaths resolves through. Two calls to GetFolderPath either side of that mutation would not
// agree, and this class compares exactly that pair.
[Collection(SpecialFolderRoots.Name)]
public class UnlockerPathsTests
{
    [Fact]
    public void Overridden_roots_place_every_derived_path_under_them()
    {
        var paths = new UnlockerPaths("/roaming", "/common");

        Assert.Equal(Path.Combine("/roaming", "anadius", "EA DLC Unlocker v2"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(paths.ConfigDirectory, "config.ini"), paths.ConfigFile);
        Assert.Equal(Path.Combine(paths.ConfigDirectory, "g_The Sims 4.ini"), paths.DlcListFile);
        Assert.Equal(Path.Combine("/common", "EA Desktop", "machine.ini"), paths.MachineIniFile);
    }

    [Fact]
    public void Unset_roots_fall_back_to_the_platform_special_folders()
    {
        var paths = new UnlockerPaths();

        // Rooted is asserted on its own, and DoNotVerify is used to draw the expectations below,
        // for the reason AppPaths documents: without the option GetFolderPath answers with an empty
        // string for a directory that does not exist yet, which makes both roots relative and makes
        // an expectation drawn from that same answer agree with them.
        Assert.True(Path.IsPathRooted(paths.Roaming), $"'{paths.Roaming}' is not an absolute path");
        Assert.True(Path.IsPathRooted(paths.CommonAppData),
                    $"'{paths.CommonAppData}' is not an absolute path");

        Assert.Equal(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            paths.Roaming);
        Assert.Equal(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            paths.CommonAppData);
    }

    /// <summary>
    /// The pair that matters: the backup must be under the app root, NOT under the unlocker config
    /// directory that removal deletes. Asserting only "it is a path" would pass either way.
    /// </summary>
    [Fact]
    public void The_autostart_backup_lives_outside_the_directory_removal_deletes()
    {
        var app = new AppPaths("/app-root");
        var unlocker = new UnlockerPaths("/roaming", "/common");

        Assert.StartsWith("/app-root", app.UnlockerAutostartBackupFile);
        Assert.DoesNotContain("anadius", app.UnlockerAutostartBackupFile);
        Assert.False(app.UnlockerAutostartBackupFile.StartsWith(unlocker.ConfigDirectory,
                                                               StringComparison.Ordinal));
    }
}
