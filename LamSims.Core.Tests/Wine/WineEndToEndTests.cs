using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

/// <summary>
/// A real Lutris config naming a real fake prefix, discovered rather than handed over, driven
/// through the real engine with a fake asset source. Nothing here is a stub except the DLL bytes and
/// the process reader: this is the only test that proves discovery, resolution, the engine and the
/// override are wired to each other rather than merely correct on their own.
/// </summary>
public class WineEndToEndTests
{
    [LinuxFact]
    public async Task A_discovered_prefix_installs_and_removes_cleanly()
    {
        using var dir = new TempDir();

        var home = Path.Combine(dir.Path, "home");
        var prefixRoot = Path.Combine(dir.Path, "mnt", "games", "ea-app");
        WinePrefixScannerTests.Prefix(prefixRoot);
        Directory.CreateDirectory(Path.Combine(prefixRoot, "drive_c", "ProgramData"));
        // PathsFor requires both the user directory and its AppData\Roaming subdirectory: AppData is an
        // intermediate segment in the path resolution, so ResolveUnder returns null if it is missing,
        // which causes Inner to return null and kills detection before targets are produced.
        Directory.CreateDirectory(Path.Combine(prefixRoot, "drive_c", "users", "playday", "AppData", "Roaming"));

        const string windows =
            @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe";
        var clientDirectory = Path.Combine(prefixRoot, "drive_c", "Program Files",
                                           "Electronic Arts", "EA Desktop", "EA Desktop");
        Directory.CreateDirectory(clientDirectory);
        File.WriteAllBytes(Path.Combine(clientDirectory, "EADesktop.exe"), [0x4D, 0x5A]);

        File.WriteAllText(Path.Combine(prefixRoot, "system.reg"),
            "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
            + $"\"ClientPath\"=\"{windows.Replace("\\", "\\\\")}\"\n");

        const string emptyBlock =
            "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Wine\\\\DllOverrides] 0\n";
        var userReg = Path.Combine(prefixRoot, "user.reg");
        File.WriteAllText(userReg, emptyBlock);

        // Discovery has to find the prefix from this file alone. Nothing tells the scanner the path.
        var games = Path.Combine(home, ".local", "share", "lutris", "games");
        Directory.CreateDirectory(games);
        File.WriteAllText(Path.Combine(games, "ea-app-1778070803.yml"),
                          $"name: EA app\ngame_slug: ea-app\nprefix: {prefixRoot}\nrunner: wine\n");

        var app = new AppPaths(Path.Combine(dir.Path, "app"));
        app.EnsureCreated();

        var source = Path.Combine(dir.Path, "cache", "version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [0x4D, 0x5A, 0x90, 0x00]);

        var notes = new Notes();
        var backend = new WinePrefixUnlockerBackend(
            new LauncherHomes(home, null, null, null), app, new FakeDelayProvider(),
            new FakeWineProcesses(), "playday", () => null, notes);

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Single(targets);
        Assert.Equal("EA app", targets[0].Environment!.Detail);
        Assert.Equal(EnvironmentSource.Lutris, targets[0].Environment!.Source);

        var progress = new SyncProgress<UnlockerProgress>(_ => { });
        var installed = await backend.InstallAsync(targets[0], new StubAssetSource(source),
                                                   progress, CancellationToken.None);

        Assert.True(installed.Success, installed.Error);
        Assert.True(File.Exists(Path.Combine(clientDirectory, "version.dll")));
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(userReg, WineDllOverride.Key, "*version")?.Text);
        Assert.Equal(UnlockerState.Installed,
                     (await backend.GetStatusAsync(targets[0], CancellationToken.None)).State);

        var removed = await backend.RemoveAsync(targets[0], progress, CancellationToken.None);

        Assert.True(removed.Success, removed.Error);
        Assert.False(File.Exists(Path.Combine(clientDirectory, "version.dll")));
        Assert.Equal(emptyBlock, await File.ReadAllTextAsync(userReg));
        Assert.Equal(UnlockerState.NotInstalled,
                     (await backend.GetStatusAsync(targets[0], CancellationToken.None)).State);
    }

    /// <summary>
    /// Detection on a machine with no prefixes at all is empty and quiet — not an error. A build
    /// that bannered here would nag every Windows user and every Linux user who has no Wine.
    /// </summary>
    [LinuxFact]
    public async Task A_machine_with_no_prefixes_detects_nothing_and_does_not_fail()
    {
        using var dir = new TempDir();
        var app = new AppPaths(Path.Combine(dir.Path, "app"));
        app.EnsureCreated();

        var notes = new Notes();
        var log = new RecordingLogSink();
        var backend = new WinePrefixUnlockerBackend(
            new LauncherHomes(Path.Combine(dir.Path, "empty-home"), null, null, null), app,
            new FakeDelayProvider(), new FakeWineProcesses(), "playday", () => null, notes, log);

        Assert.Empty(await backend.DetectTargetsAsync(CancellationToken.None));

        // No REJECTION at all, in either channel: nothing was examined, so nothing can be
        // complained about. The rejection line is asserted against the LOG, which is where it goes
        // now — against the notes it would hold whether the routing worked or not. Not Assert.Empty
        // on the whole note list — the scanner's sandbox diagnostic keys off /.flatpak-info, which
        // exists when the suite itself runs inside a Flatpak, and that note is correct there.
        Assert.False(log.Logged("is not a Wine prefix"), string.Join("\n", log.Texts));
        Assert.False(log.Logged("no EA app or Origin"), string.Join("\n", log.Texts));
    }

    /// <summary>
    /// The shipped complaint, as a test. A real Linux machine hands the scanner far more paths than
    /// are prefixes — every Steam compatdata container, every Heroic game directory — and a prefix
    /// that IS one usually holds no EA client. Every one of those used to put a line in the window's
    /// note list, above the unlocker's own buttons: the reported symptom was thirty lines of
    /// "'/…/compatdata/220/pfx' is not a Wine prefix" and an Install button arranged off screen.
    ///
    /// The note list is now for what a user must act on, so on this machine it holds exactly one
    /// thing — that nothing was found, and where to point the setting. The log holds the reasoning.
    /// Both halves matter: notes-only would pass against a build that reported nothing anywhere,
    /// and log-only would pass against one that still showed every line.
    /// </summary>
    [LinuxFact]
    public async Task A_machine_full_of_directories_that_are_not_prefixes_says_so_once()
    {
        using var dir = new TempDir();
        var app = new AppPaths(Path.Combine(dir.Path, "app"));
        app.EnsureCreated();

        var home = Path.Combine(dir.Path, "home");
        var compatdata = Path.Combine(home, ".local", "share", "Steam", "steamapps", "compatdata");
        foreach (var appId in new[] { "220", "440", "570", "730", "1222670" })
            Directory.CreateDirectory(Path.Combine(compatdata, appId, "pfx"));

        var notes = new Notes();
        var log = new RecordingLogSink();
        var backend = new WinePrefixUnlockerBackend(
            new LauncherHomes(home, null, null, null), app,
            new FakeDelayProvider(), new FakeWineProcesses(), "playday", () => null, notes, log);

        Assert.Empty(await backend.DetectTargetsAsync(CancellationToken.None));

        // Not Assert.Empty on the note list — the sandbox diagnostic keys off /.flatpak-info, which
        // exists when the suite itself runs inside a Flatpak, and that note is correct there.
        Assert.DoesNotContain(notes.Lines, l => l.Contains("is not a Wine prefix", StringComparison.Ordinal));
        Assert.Single(notes.Lines, l => l.Contains("No Wine prefix was found", StringComparison.Ordinal));

        // The reasoning is not discarded, it is filed: one line per container the scan looked at.
        Assert.Equal(5, log.Texts.Count(t => t.Contains("is not a Wine prefix", StringComparison.Ordinal)));
    }
}
