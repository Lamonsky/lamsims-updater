using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.ViewModels;
using LamSims.Core.Queueing;

namespace LamSims.App.Tests;

public class MainViewModelSettingsTests
{
    [Fact(Timeout = 15000)]
    public async Task Browsing_for_a_game_folder_sets_it_saves_it_and_rescans()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        host.Pickers.NextFolder = host.GameDirectory;

        await vm.BrowseGameCommand.ExecuteAsync(null);

        Assert.Equal(host.GameDirectory, vm.GameDirectory);
        Assert.True(vm.ScanCount > 0);
        Assert.Single(host.Saved);
    }

    [Fact(Timeout = 15000)]
    public async Task A_cancelled_picker_changes_nothing()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.GameDirectory = host.GameDirectory;
        host.Pickers.NextFolder = null;

        await vm.BrowseGameCommand.ExecuteAsync(null);

        Assert.Equal(host.GameDirectory, vm.GameDirectory);
        Assert.Empty(host.Saved);
    }

    [Fact(Timeout = 15000)]
    public async Task A_change_is_not_written_until_the_debounce_has_elapsed()
    {
        // A slider drag must not write settings.json once per pixel. The clock is injected so
        // this test moves time instead of sleeping.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.Connections = 2;
        vm.Connections = 4;
        vm.Connections = 8;

        await vm.FlushDueSettingsAsync(CancellationToken.None);
        Assert.Empty(host.Saved);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await vm.FlushDueSettingsAsync(CancellationToken.None);

        Assert.Single(host.Saved);
        Assert.Equal(8, host.Saved[0].Connections);
        Assert.Equal(1, vm.SettingsWriteCount);
    }

    [Fact(Timeout = 15000)]
    public async Task A_new_connection_count_reaches_the_engine_without_a_restart()
    {
        // Saving alone would also hold for a value that only takes effect on the next launch.
        // The options instance is the one SegmentedDownloader reads per download, so it is what
        // says the change is live.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.Connections = 12;

        Assert.Equal(12, host.DownloadOptions.Connections);

        await vm.FlushSettingsNowAsync(CancellationToken.None);
        Assert.Equal(12, host.Saved[0].Connections);
    }

    [Fact(Timeout = 15000)]
    public async Task Choosing_a_download_folder_moves_the_engine_to_it_without_a_restart()
    {
        // This is the defect that prompted the change: the folder was recorded in settings.json
        // and the downloads kept going to the old root, because the graph was built before the
        // setting was read and nothing re-read it.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        var chosen = Path.Combine(host.Root, "somewhere-else");
        host.Pickers.NextFolder = chosen;

        await vm.BrowseDownloadsCommand.ExecuteAsync(null);

        Assert.Equal(chosen, host.DownloadPaths.Root);
        Assert.Equal(Path.Combine(chosen, "EP01.part"), host.DownloadPaths.PartFile("EP01"));
        Assert.True(Directory.Exists(chosen));
        Assert.Equal(chosen, host.Saved[0].DownloadDirectory);
    }

    [Fact(Timeout = 15000)]
    public async Task A_download_folder_that_cannot_be_created_banners_and_leaves_the_old_one_in_use()
    {
        // The shipped case: a directory on a drive that is not mounted. Silently continuing on
        // the old root would tell the user nothing.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        var before = host.DownloadPaths.Root;
        var blocked = Path.Combine(host.Root, "a-file-not-a-directory");
        File.WriteAllText(blocked, "");
        host.Pickers.NextFolder = Path.Combine(blocked, "downloads");

        await vm.BrowseDownloadsCommand.ExecuteAsync(null);

        Assert.Equal(before, host.DownloadPaths.Root);
        Assert.Contains(vm.Banners, b => b.Id == "downloads" && b.Kind == BannerKind.Warning);
    }

    [Fact(Timeout = 15000)]
    public async Task A_refused_download_folder_is_not_written_to_settings()
    {
        // Bannering is not enough on its own. If the refused path still reaches settings.json, the
        // next launch fails to create it too and falls back to the default, so the directory that
        // was working is lost along with every archive already in it.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        var working = Path.Combine(host.Root, "working");
        host.Pickers.NextFolder = working;
        await vm.BrowseDownloadsCommand.ExecuteAsync(null);
        Assert.Equal(working, host.DownloadPaths.Root);
        Assert.Single(host.Saved);

        var blocked = Path.Combine(host.Root, "a-file-not-a-directory");
        File.WriteAllText(blocked, "");
        host.Pickers.NextFolder = Path.Combine(blocked, "downloads");
        await vm.BrowseDownloadsCommand.ExecuteAsync(null);

        Assert.Equal(working, host.DownloadPaths.Root);
        Assert.Equal(working, vm.DownloadDirectory);
        Assert.Equal(working, host.Saved[^1].DownloadDirectory);
        Assert.Single(host.Saved);
    }

    [Fact]
    public void Moving_the_download_folder_is_refused_while_work_is_in_the_queue()
    {
        // The .part file and the lock a live run holds are both derived from the root. Moving it
        // under that run abandons both where nothing will look for them again.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running,
            [new QueueItemSnapshot("EP01", "EP01", QueueItemState.Downloading, 0, 0, 0, null, null, null, Array.Empty<string>())]));
        Assert.False(vm.BrowseDownloadsCommand.CanExecute(null));

        vm.ApplyUpdate(new QueueUpdate(QueueState.Idle,
            [new QueueItemSnapshot("EP01", "EP01", QueueItemState.Completed, 0, 0, 0, null, null, null, Array.Empty<string>())]));
        Assert.True(vm.BrowseDownloadsCommand.CanExecute(null));
    }

    [Fact(Timeout = 15000)]
    public async Task Settings_loaded_at_startup_are_shown_rather_than_overwritten_by_defaults()
    {
        // Seeding through the property setters would queue a save for a value the user already
        // chose and, now that the setters apply their setting, would retarget the engine onto a
        // directory composition had already pointed it at. The empty save is the observable for
        // both: nothing was queued, so no setter ran.
        var vm = TestHost.ViewModel(out var host, seed: settings =>
        {
            settings.Connections = 16;
            settings.DownloadDirectory = "/tmp/downloads";
        });
        using var _h = host;

        Assert.Equal(16, vm.Connections);
        Assert.Equal("/tmp/downloads", vm.DownloadDirectory);

        await vm.FlushSettingsNowAsync(CancellationToken.None);
        Assert.Empty(host.Saved);
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_save_becomes_a_banner_rather_than_an_exception()
    {
        var vm = TestHost.ViewModel(out var host,
            save: (_, _) => throw new IOException("the settings file could not be written"));
        using var _h = host;

        vm.Connections = 3;
        await vm.FlushSettingsNowAsync(CancellationToken.None);

        Assert.Contains(vm.Banners, b => b.Id == "settings" && b.Kind == BannerKind.Warning);
    }
}
