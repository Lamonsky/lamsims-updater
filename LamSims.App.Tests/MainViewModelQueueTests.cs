using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.Linq;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class MainViewModelQueueTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state, bool isFinal = false) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>()) { IsFinal = isFinal };

    [Fact]
    public void Search_filters_on_code_and_name_case_insensitively()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;
        vm.BuildRows([Packs.Entry(), Packs.Entry("EP02", "Get Together")]);

        vm.SearchText = "together";
        Assert.Single(vm.FilteredRows);
        Assert.Equal("EP02", vm.FilteredRows[0].Code);

        vm.SearchText = "ep0";
        Assert.Equal(2, vm.FilteredRows.Count);
    }

    [Fact]
    public void Select_all_checks_only_the_checkable_rows_that_the_search_shows()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;
        vm.BuildRows([Packs.Entry(), Packs.Entry("EP02", "Get Together"), Packs.Entry("SP01", "Luxury Party")]);
        vm.Rows[1].ApplyScan(new PackScanResult("EP02", PackInstallState.Installed, [], null, null));
        vm.SearchText = "EP";        // hides SP01

        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.Rows[0].IsChecked);
        Assert.False(vm.Rows[1].IsChecked);   // installed, so not checkable
        Assert.False(vm.Rows[2].IsChecked);   // filtered out

        vm.SelectNoneCommand.Execute(null);
        Assert.All(vm.Rows, r => Assert.False(r.IsChecked));
    }

    [Fact]
    public void Add_enqueues_the_checked_rows_in_list_order_and_clears_them()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;
        vm.BuildRows([Packs.Entry(), Packs.Entry("EP02", "Get Together")]);
        vm.GameDirectory = host.GameDirectory;
        vm.Rows[0].IsChecked = true;
        vm.Rows[1].IsChecked = true;

        vm.AddSelectedCommand.Execute(null);

        Assert.Equal(["EP01", "EP02"], host.Queue.Enqueued.Select(e => e.Code));
        Assert.All(host.Queue.Enqueued, e => Assert.Equal(host.GameDirectory, e.GameDirectory));
        Assert.All(vm.Rows, r => Assert.False(r.IsChecked));
    }

    [Fact]
    public void Add_is_refused_without_a_game_directory()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;
        vm.BuildRows([Packs.Entry()]);
        vm.Rows[0].IsChecked = true;

        Assert.False(vm.AddSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Add_is_refused_when_the_game_directory_could_not_be_read()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;
        vm.GameDirectory = host.GameDirectory;
        vm.GameDirectoryReadable = false;

        Assert.False(vm.AddSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void The_queue_controls_follow_the_queue_state()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;

        vm.ApplyQueueState(QueueState.Idle);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.ResumeCommand.CanExecute(null));

        vm.ApplyQueueState(QueueState.Running);
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.ResumeCommand.CanExecute(null));

        vm.ApplyQueueState(QueueState.Pausing);
        Assert.True(vm.ResumeCommand.CanExecute(null));

        vm.ApplyQueueState(QueueState.Paused);
        Assert.True(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));
    }

    [Fact]
    public void The_queue_controls_reach_the_queue_method_they_name()
    {
        // The test above proves only enablement. Pause() calling Resume(), or CancelAllCommand
        // wired to the forbidden Complete(), passes it.
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));

        vm.PauseCommand.Execute(null);
        vm.ApplyQueueState(QueueState.Paused);
        vm.ResumeCommand.Execute(null);
        vm.CancelAllCommand.Execute(null);

        Assert.Equal(["Pause", "Resume", "CancelAll"], host.Queue.Calls);
    }

    [Fact]
    public void The_counter_reports_what_is_left_rather_than_what_is_done()
    {
        // Core retains every item ever enqueued, so a "done" counter climbs all session and
        // reads "1 of 30" by the afternoon. Pending and failed are bounded by what the user can
        // still act on.
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running,
        [
            Snap("EP01", QueueItemState.Completed),
            Snap("EP02", QueueItemState.Downloading),
            Snap("EP03", QueueItemState.Queued),
            Snap("EP04", QueueItemState.Failed),
        ]));

        Assert.Equal(2, vm.PendingCount);
        Assert.Equal(1, vm.FailedCount);
        Assert.Equal("Running — 2 left, 1 failed", vm.StatusText);
    }

    [Fact]
    public void A_pack_the_queue_has_given_up_on_stops_counting_as_work_left()
    {
        // A twice-blocked pack is terminal to the queue, so counting it as pending pins
        // CanChangeCatalog false for the rest of the session and takes away the one escape left.
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Idle,
            [Snap("EP01", QueueItemState.Blocked, isFinal: true)]));

        Assert.Equal(0, vm.PendingCount);
        Assert.True(vm.CanChangeCatalog);
    }

    [Fact]
    public void A_pack_still_awaiting_its_retry_does_count_as_work_left()
    {
        // The contrast: a first block IS pending work, and changing the catalog under it would
        // leave the queue running a pack this window no longer lists.
        var vm = TestHost.ViewModel(out var host);
        using var _ = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Idle,
            [Snap("EP01", QueueItemState.Blocked, isFinal: false)]));

        Assert.Equal(1, vm.PendingCount);
        Assert.False(vm.CanChangeCatalog);
    }
}
