using System;
using System.IO;
using Xunit;
using LamSims.Core.Downloading;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

/// <summary>
/// The collection every class that builds a paths object on the real environment shares:
/// AppPathsTests, DownloadPathsTests and UnlockerPathsTests. AppPathsTests mutates the XDG
/// variables all three resolve through, so none of them may run beside it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SpecialFolderRoots
{
    public const string Name = "special-folder roots";
}

[Collection(SpecialFolderRoots.Name)]
public class AppPathsTests
{
    // Three assertions, each catching what the others cannot. Rooted holds on every platform and is
    // the only one an empty GetFolderPath answer fails: asked without DoNotVerify that is what a
    // configuration directory which does not exist yet produces, and a suffix comparison drawn from
    // the same empty answer agrees with the relative Root it makes. The suffix, checked against the
    // same call AppPaths makes, cannot catch a wrong DIRECTORY but does catch a Root that forgot to
    // name the application and would drop settings straight into the user's configuration
    // directory. Where the platform's answer is a documented constant — Windows and Linux — it is
    // then spelled out independently, which is what pins the directory itself. macOS is left to the
    // first two: .NET's answer there is not a constant this test should assert from memory.
    [Fact]
    public void Defaults_under_the_users_config_directory()
    {
        var paths = new AppPaths();

        Assert.True(Path.IsPathRooted(paths.Root), $"'{paths.Root}' is not an absolute path");

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        Assert.Equal(Path.Combine(appData, "lamsims-updater"), paths.Root);

        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(home, "AppData", "Roaming", "lamsims-updater"), paths.Root);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg,
                             "lamsims-updater"),
                paths.Root);
        }
    }

    /// <summary>
    /// The condition DoNotVerify exists for, arranged rather than waited for. Asked without it,
    /// GetFolderPath answers with an empty string for a directory that does not exist yet, and
    /// Path.Combine turns that into a relative root: every settings file, cached catalog, install
    /// marker, pack archive and part file would then follow the process's working directory.
    ///
    /// Both classes are asserted here because both resolve through the variables this test sets,
    /// and setting them is process-wide — see SpecialFolderRoots for what keeps that safe.
    ///
    /// Linux only because the XDG variables are the arrangement: Windows resolves %APPDATA%, which
    /// always exists, and macOS answers from its own roots and reads neither variable.
    /// </summary>
    [LinuxFact("Linux only: the XDG variables are what make a missing directory arrangeable.")]
    public void Both_roots_stay_absolute_when_their_directories_do_not_exist_yet()
    {
        using var temp = new TempDir();
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(temp.Path, "no-config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(temp.Path, "no-data"));

            var appRoot = new AppPaths().Root;
            var downloadRoot = new DownloadPaths().DefaultRoot;

            Assert.True(Path.IsPathRooted(appRoot), $"the app data root '{appRoot}' is not absolute");
            Assert.True(Path.IsPathRooted(downloadRoot),
                        $"the download root '{downloadRoot}' is not absolute");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
        }
    }

    [Fact]
    public void Names_the_settings_and_cache_files_inside_the_root()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(temp.Path, "catalog.cache.json"), paths.CatalogCacheFile);
    }

    [Fact]
    public void Creates_the_root_on_demand()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "nested", "root"));

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Root));
    }

    [Fact]
    public void Names_the_install_state_directory_inside_the_root()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "installs"), paths.InstallStateDirectory);
    }
}
