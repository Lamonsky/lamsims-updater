using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Logging;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class SteamDiscoveryTests
{
    private static string App(string steamRoot, string appId, string? version = null,
                             string? configInfo = null)
    {
        var container = Path.Combine(steamRoot, "steamapps", "compatdata", appId);
        WinePrefixScannerTests.Prefix(Path.Combine(container, "pfx"), user: "steamuser");

        if (version is not null) File.WriteAllText(Path.Combine(container, "version"), version);
        if (configInfo is not null) File.WriteAllText(Path.Combine(container, "config_info"), configInfo);

        return Path.Combine(container, "pfx");
    }

    private static WinePrefixScanner Scanner(TempDir dir, Notes notes,
                                             RecordingLogSink? log = null) =>
        new(new LauncherHomes(dir.Path, null, null, null), "playday", notes,
            log: log ?? new RecordingLogSink());

    private static string Root(TempDir dir)
    {
        var root = Path.Combine(dir.Path, ".local", "share", "Steam");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Valve Proton's prefix is a real `pfx/` subdirectory, and the marker files sit beside it, so
    /// this also proves the Proton-managed detection reaches one level up.
    /// </summary>
    [Fact]
    public void A_compatdata_prefix_is_found_and_labelled_with_its_app_id()
    {
        using var dir = new TempDir();
        App(Root(dir), "1222670", configInfo: "GE-Proton10-34\n");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Equal(EnvironmentSource.Steam, found[0].Environment.Source);
        Assert.Equal("GE-Proton10-34, app 1222670", found[0].Environment.Detail);
        Assert.True(found[0].ProtonManaged);
    }

    /// <summary>
    /// compatdata/0 is not an app. Without the skip every machine with Steam reports a phantom
    /// target, or a rejection note about one, that the user cannot act on.
    /// </summary>
    [Fact]
    public void Compatdata_zero_is_skipped()
    {
        using var dir = new TempDir();
        var root = Root(dir);
        App(root, "0");
        App(root, "1222670");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Contains("1222670", found[0].Environment.Detail);
    }

    /// <summary>
    /// A second library on another disk is the ordinary case for a Steam user, and its prefixes are
    /// not under the root at all. Scanning only the root finds none of them.
    /// </summary>
    [Fact]
    public void Prefixes_in_a_second_library_folder_are_found()
    {
        using var dir = new TempDir();
        var root = Root(dir);
        var library = Path.Combine(dir.Path, "mnt", "games", "SteamLibrary");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t"
            + $"\"{root.Replace("\\", "\\\\")}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t"
            + $"\"{library.Replace("\\", "\\\\")}\"\n\t}}\n}}\n");
        App(library, "2000000");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Contains("2000000", found[0].Environment.Detail);
    }

    /// <summary>
    /// The pair that matters: the build is NOT config_info line 1 in general. GE-Proton writes
    /// its own name there, Valve Proton writes a version number and the build is only in the later
    /// path lines. Taking line 1 unconditionally labels every Valve prefix "11.0-100", which is not
    /// a build the user can look up.
    /// </summary>
    [Fact]
    public void The_proton_build_comes_from_the_config_info_path_lines()
    {
        using var dir = new TempDir();
        App(Root(dir), "1262600", version: "11.0-100\n",
            configInfo: "11.0-100\n/home/x/.steam/steam/steamapps/common/Proton - Experimental/files/lib\n");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Equal("Proton - Experimental, app 1262600", found[0].Environment.Detail);
    }

    [Fact]
    public void With_no_config_info_the_raw_version_string_is_used()
    {
        using var dir = new TempDir();
        App(Root(dir), "1222670", version: "GE-Proton10-34\n");
        var notes = new Notes();

        Assert.Equal("GE-Proton10-34, app 1222670",
                     Scanner(dir, notes).Scan(null)[0].Environment.Detail);
    }

    [Fact]
    public void With_neither_file_the_app_id_alone_is_the_label()
    {
        using var dir = new TempDir();
        App(Root(dir), "1222670");
        var notes = new Notes();

        Assert.Equal("app 1222670", Scanner(dir, notes).Scan(null)[0].Environment.Detail);
    }

    /// <summary>
    /// A Flatpak Steam keeps its own library list, and the flag is what separates its row from a
    /// native install's when both have a prefix for the same app.
    /// </summary>
    [Fact]
    public void A_flatpak_steam_is_scanned_and_flagged()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, ".var", "app", "com.valvesoftware.Steam",
                                ".local", "share", "Steam");
        Directory.CreateDirectory(root);
        App(root, "1222670");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.True(found[0].Environment.Flatpak);
    }

    /// <summary>
    /// Steam's libraryfolders.vdf lists the primary library as entry "0" with path equal to the root.
    /// Libraries() must deduplicate resolved paths to avoid yielding the root twice, which would cause
    /// every primary-library app to produce two candidates and get labelled "2 games" instead of its
    /// Proton build and app id.
    /// </summary>
    [Fact]
    public void An_app_under_the_primary_library_is_not_mislabelled_as_two_games()
    {
        using var dir = new TempDir();
        var root = Root(dir);
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t"
            + $"\"{root.Replace("\\", "\\\\")}\"\n\t}}\n}}\n");
        App(root, "1222670", configInfo: "GE-Proton10-34\n");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Equal("GE-Proton10-34, app 1222670", found[0].Environment.Detail);
    }

    [PosixDenialFact]
    public void An_unreadable_library_list_yields_a_diagnostic_and_no_exception()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var root = Root(dir);
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        var vdfPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(vdfPath, "\"libraryfolders\"\n{\n}\n");
        File.SetUnixFileMode(vdfPath, System.IO.UnixFileMode.None);
        App(root, "1222670");
        var notes = new Notes();

        // The LOG. An unreadable libraryfolders.vdf is a fact about Steam's own file: the scan
        // carries on without it, and the window's note list is for what a user must act on.
        var log = new RecordingLogSink();

        try
        {
            var found = Scanner(dir, notes, log).Scan(null);

            Assert.Single(found);
            Assert.True(log.Logged("could not be read"), string.Join("\n", log.Texts));
        }
        finally
        {
            File.SetUnixFileMode(vdfPath, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
        }
    }
}
