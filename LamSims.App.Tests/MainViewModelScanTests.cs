using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.IO;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class MainViewModelScanTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    private static void Deliver(MainViewModel vm, QueueUpdate update)
    {
        // Exactly what QueueBridge.Apply does: rows first, then the view model.
        foreach (var item in update.Items) vm.RowFor(item.Code)?.ApplyQueue(item);

        vm.ApplyUpdate(update);
    }

    [Fact]
    public void A_scan_applies_disk_state_to_every_row()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry(), Packs.Entry("EP02", "Get Together")]);
        vm.GameDirectory = host.GameDirectory;
        Directory.CreateDirectory(Path.Combine(host.GameDirectory, "EP01"));

        vm.ScanCommand.Execute(null);

        Assert.Equal(PackInstallState.InstalledUnverified, vm.Rows[0].InstallState);
        Assert.Equal(PackInstallState.NotInstalled, vm.Rows[1].InstallState);
    }

    [Fact]
    public void An_unreadable_game_directory_is_a_banner_and_disables_adding()
    {
        // Every pack reads NotInstalled when the directory cannot be read, so without the flag
        // an unreadable directory is indistinguishable from an empty one.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = Path.Combine(host.Root, "no-such-directory");

        vm.ScanCommand.Execute(null);

        Assert.False(vm.GameDirectoryReadable);
        Assert.Contains(vm.Banners, b => b.Id == "game-directory");
        Assert.False(vm.AddSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void A_readable_directory_clears_the_banner_again()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = Path.Combine(host.Root, "no-such-directory");
        vm.ScanCommand.Execute(null);

        vm.GameDirectory = host.GameDirectory;
        vm.ScanCommand.Execute(null);

        Assert.True(vm.GameDirectoryReadable);
        Assert.DoesNotContain(vm.Banners, b => b.Id == "game-directory");
    }

    [Fact]
    public void A_completed_pack_triggers_a_rescan_so_the_row_reads_disk_truth()
    {
        // The queue's word for a finished pack is "Completed"; the marker the install wrote is
        // what says Installed. The rescan is also what clears the terminal overlay.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = host.GameDirectory;

        Deliver(vm, new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        Assert.Equal(QueueItemState.Downloading, vm.Rows[0].QueueState);

        Directory.CreateDirectory(Path.Combine(host.GameDirectory, "EP01"));
        Deliver(vm, new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]));

        Assert.Null(vm.Rows[0].QueueState);
        Assert.Equal("Installed (not verified by this tool)", vm.Rows[0].StatusText);
    }

    [Fact]
    public void A_pack_that_completes_a_second_time_rescans_a_second_time()
    {
        // The dedupe set below is needed (every update carries every item), but it has to be
        // RELEASED as well as taken. A reinstall goes Completed, then Queued, then Completed
        // again; with the code left in the set the second completion fires no rescan at all, so
        // the terminal overlay is never retired and the row sits at "Installed" with a dead
        // checkbox until the user finds the Scan button. That is the normal outcome of every
        // Reinstall, which is the context menu's whole purpose.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = host.GameDirectory;

        var completed = new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]);

        Deliver(vm, completed);
        var afterFirst = vm.ScanCount;

        // A genuine re-enqueue: Reset publishes Queued before anything else.
        Deliver(vm, new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Queued)]));
        Deliver(vm, completed);

        Assert.Equal(afterFirst + 1, vm.ScanCount);
        Assert.Null(vm.Rows[0].QueueState);
    }

    [Fact]
    public void Rebuilding_the_rows_forgets_which_packs_have_already_been_rescanned()
    {
        // BuildRows drops every row, so each code in the rescan dedupe is a claim about something
        // that no longer exists. Left standing, a pack completed before a catalog change and
        // enqueued again afterwards has its rebuilt row adopt an echoed Completed with no rescan
        // to retire the overlay, so the row pins "Installed" with a dead checkbox.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.GameDirectory = host.GameDirectory;

        var completed = new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]);

        vm.BuildRows([Packs.Entry()]);
        Deliver(vm, completed);
        var afterFirst = vm.ScanCount;

        vm.BuildRows([Packs.Entry()]);
        Deliver(vm, completed);

        Assert.Equal(afterFirst + 1, vm.ScanCount);
        Assert.Null(vm.Rows[0].QueueState);
    }

    [Fact]
    public void A_second_update_naming_the_same_completion_does_not_rescan_again()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = host.GameDirectory;

        var completed = new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]);

        Deliver(vm, completed);
        var after = vm.ScanCount;
        Deliver(vm, completed);

        Assert.Equal(after, vm.ScanCount);
    }

    [Fact]
    public void The_saves_folder_is_a_warning_that_names_the_folder_to_choose_instead()
    {
        // The directory users actually pick: same name as the installation, perfectly readable, and
        // every pack installed into it stays invisible to the game.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        var saves = Path.Combine(host.Root, "Documents", "Electronic Arts", "The Sims 4");
        Directory.CreateDirectory(Path.Combine(saves, "Mods"));
        Directory.CreateDirectory(Path.Combine(saves, "Tray"));
        vm.GameDirectory = saves;

        vm.ScanCommand.Execute(null);

        var banner = Assert.Single(vm.Banners, b => b.Id == "game-root");
        Assert.Equal(BannerKind.Warning, banner.Kind);
        Assert.Contains("saves folder", banner.Text);
        Assert.Contains(Path.Combine("Game", "Bin"), banner.Text);
    }

    [Fact]
    public void A_folder_matching_no_known_layout_is_a_warning_too()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        var elsewhere = Path.Combine(host.Root, "somewhere-else");
        Directory.CreateDirectory(elsewhere);
        vm.GameDirectory = elsewhere;

        vm.ScanCommand.Execute(null);

        var banner = Assert.Single(vm.Banners, b => b.Id == "game-root");
        Assert.Equal(BannerKind.Warning, banner.Kind);
        Assert.Contains("does not look like", banner.Text);
    }

    [Fact]
    public void The_warning_never_stops_a_pack_being_added()
    {
        // A warning, not a gate: the check has to be able to be wrong about a real installation
        // without costing the user the install.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        var elsewhere = Path.Combine(host.Root, "somewhere-else");
        Directory.CreateDirectory(elsewhere);
        vm.GameDirectory = elsewhere;

        vm.ScanCommand.Execute(null);

        Assert.Contains(vm.Banners, b => b.Id == "game-root");
        Assert.True(vm.AddSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Choosing_a_real_installation_clears_an_earlier_warning()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        var elsewhere = Path.Combine(host.Root, "somewhere-else");
        Directory.CreateDirectory(elsewhere);
        vm.GameDirectory = elsewhere;
        vm.ScanCommand.Execute(null);
        Assert.Contains(vm.Banners, b => b.Id == "game-root");

        vm.GameDirectory = host.GameDirectory;
        vm.ScanCommand.Execute(null);

        Assert.DoesNotContain(vm.Banners, b => b.Id == "game-root");
        Assert.True(vm.AddSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void An_unreadable_directory_replaces_the_layout_warning_rather_than_joining_it()
    {
        // Two banners for one directory read as two problems, and the layout of a directory that
        // cannot be read at all is the smaller of the two claims.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        var elsewhere = Path.Combine(host.Root, "somewhere-else");
        Directory.CreateDirectory(elsewhere);
        vm.GameDirectory = elsewhere;
        vm.ScanCommand.Execute(null);
        Assert.Contains(vm.Banners, b => b.Id == "game-root");

        vm.GameDirectory = Path.Combine(host.Root, "no-such-directory");
        vm.ScanCommand.Execute(null);

        Assert.Contains(vm.Banners, b => b.Id == "game-directory");
        Assert.DoesNotContain(vm.Banners, b => b.Id == "game-root");
    }
}
