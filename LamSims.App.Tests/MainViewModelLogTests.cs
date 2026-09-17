using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.ViewModels;
using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;

namespace LamSims.App.Tests;

public class MainViewModelLogTests
{
    /// <summary>
    /// Design §2's App line, and the pair distinguishes both counts at once: a scan that mixed up
    /// which count is "installed" and which is "partial", or that folded NotInstalled into either
    /// one, fails this. InstalledUnverified counts as installed (InstallScanner's own doc: it
    /// "belongs to the installed family and is never a warning"); the two-InstallDirs pack with
    /// only one directory present is what drives Partial without needing an install marker.
    /// </summary>
    [Fact]
    public void A_scan_logs_how_many_packs_are_installed_and_partial()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        var installed = Packs.Entry("EP01", "Get to Work");
        var partial = new PackEntry(
            "EP02", "Get Together", PackType.Expansion, 1024, null, new string('a', 64),
            [new Uri("https://example.invalid/EP02.zip")], ["EP02-base", "EP02-extra"]);
        var notInstalled = Packs.Entry("EP03", "Discover University");

        vm.BuildRows([installed, partial, notInstalled]);
        vm.GameDirectory = host.GameDirectory;

        // EP01: its one InstallDir is present, no marker -> InstalledUnverified (installed family).
        Directory.CreateDirectory(Path.Combine(host.GameDirectory, "EP01"));
        // EP02: only one of its two InstallDirs is present -> Partial.
        Directory.CreateDirectory(Path.Combine(host.GameDirectory, "EP02-base"));
        // EP03: neither InstallDir exists -> NotInstalled, counted in neither total.

        vm.ScanCommand.Execute(null);

        Assert.Contains(vm.Log.Lines, l => l.Text == "Scan found 1 installed, 1 partial");
    }

    /// <summary>A banner is the one thing a user is guaranteed to have seen; the log keeps it
    /// after the banner is dismissed.</summary>
    [Fact(Timeout = 15000)]
    public async Task A_raised_banner_is_mirrored_into_the_log_at_its_own_severity()
    {
        var vm = TestHost.ViewModel(out var host, settingsError: "settings were unreadable");
        using var _h = host;

        Assert.Contains(vm.Log.Lines, l => l.Text.Contains("settings were unreadable") && l.IsWarning);

        await vm.ShutdownAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task A_downloading_snapshot_ticks_the_log()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [
            new QueueItemSnapshot("EP01", "Get to Work", QueueItemState.Downloading,
                120, 1000, 8_100_000, null, null, null, [])
        ]));

        Assert.Contains(vm.Log.Lines, l => l.Code == "EP01" && l.Text.Contains("12%"));

        await vm.ShutdownAsync();
    }
}
