using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.Collections.Generic;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class PackRowQueueTests
{
    private static PackRowViewModel Row() => new(Packs.Entry(), new RecordingQueue());

    private static (PackRowViewModel Row, RecordingQueue Queue) RowWithQueue()
    {
        var queue = new RecordingQueue();
        return (new PackRowViewModel(Packs.Entry(), queue), queue);
    }

    private static QueueItemSnapshot Snap(
        QueueItemState state,
        long done = 0,
        long total = 0,
        double rate = 0,
        TimeSpan? eta = null,
        string? entry = null,
        string? error = null,
        IReadOnlyList<string>? warnings = null,
        bool isFinal = false) =>
        new("EP01", "Get to Work", state, done, total, rate, eta, entry, error,
            warnings ?? Array.Empty<string>()) { IsFinal = isFinal };

    private static PackScanResult Scan(PackInstallState state, params string[] missing) =>
        new("EP01", state, missing, null, null);

    [Fact]
    public void A_queued_pack_overrides_its_scan_state_and_stops_being_checkable()
    {
        // Enqueue ignores a pending or active item and publishes nothing, so offering the
        // checkbox would make Add a silent no-op.
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));
        row.ApplyQueue(Snap(QueueItemState.Queued));

        Assert.Equal("Queued", row.StatusText);
        Assert.False(row.IsCheckable);
    }

    [Fact]
    public void A_download_reports_percent_speed_and_eta()
    {
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Downloading, 500, 1000, 8_100_000, TimeSpan.FromMinutes(2)));

        Assert.Equal("Downloading  50%  8.1 MB/s  2m", row.StatusText);
        Assert.True(row.IsBusy);
        Assert.True(row.IsProgressVisible);
        Assert.Equal(50, row.ProgressPercent);
    }

    [Fact]
    public void A_download_with_no_rate_yet_omits_both_the_rate_and_the_eta()
    {
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Downloading, 500, 1000));

        Assert.Equal("Downloading  50%", row.StatusText);
    }

    [Fact]
    public void Progress_is_hidden_before_a_total_is_known()
    {
        // Verifying emits no progress at all, and no phase reports a total before its first
        // update. Dividing here would fault or render a false 100%.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Verifying));

        Assert.Equal("Verifying…", row.StatusText);
        Assert.True(row.IsBusy);
        Assert.False(row.IsProgressVisible);
    }

    [Fact]
    public void An_install_reports_percent_and_a_cancelled_pack_says_so()
    {
        // The two row states nothing else here asserts. Installing shares its format
        // with Downloading, so a swapped label is invisible until it is read literally; and
        // Cancelled is the one terminal state with no message of its own.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Installing, 300, 1000));

        Assert.Equal("Installing  30%", row.StatusText);
        Assert.True(row.IsProgressVisible);

        row.ApplyQueue(Snap(QueueItemState.Cancelled));

        Assert.Equal("Cancelled", row.StatusText);
        Assert.False(row.IsBusy);
        Assert.Null(row.Message);
    }

    [Fact]
    public void A_blocked_pack_shows_the_queues_own_message_as_its_status()
    {
        // Core writes two different texts for a first and a second block, and the second names
        // the only action that still works. QueueItemSnapshot carries no attempt count, so the
        // UI cannot tell them apart and shows core's words rather than inventing its own.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Blocked,
            error: "Another copy is working on 'EP01'. Queue it again."));

        Assert.Equal("Another copy is working on 'EP01'. Queue it again.", row.StatusText);
        Assert.Null(row.Message);
        Assert.Equal(RowMessageKind.None, row.MessageKind);
        Assert.False(row.CanCancel);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public void A_failed_pack_shows_its_error_as_a_message()
    {
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Failed, error: "the mirror returned 404"));

        Assert.Equal("Failed", row.StatusText);
        Assert.Equal("the mirror returned 404", row.Message);
        Assert.Equal(RowMessageKind.Error, row.MessageKind);
    }

    [Fact]
    public void A_failed_pack_carrying_a_warning_shows_the_failure_reason_too()
    {
        // PackWorkflow adds the quarantine warning inside ClassifyAsync and returns it alongside
        // a failed download, and PackQueue.Settle writes Warnings, Failed and Error in the same
        // lock. With the warnings arm ahead of the Failed arm this row read as a WARNING carrying
        // the quarantine text, and "the mirror returned 404", the only thing that says why the
        // pack is not installed, was never shown.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Failed,
            error: "the mirror returned 404",
            warnings: ["the archive on disk did not hash to the digest the catalog gives, so it was moved aside"]));

        Assert.Equal("Failed", row.StatusText);
        Assert.Equal(RowMessageKind.Error, row.MessageKind);
        Assert.NotNull(row.Message);
        Assert.Contains("the mirror returned 404", row.Message);
        Assert.StartsWith("the mirror returned 404", row.Message, StringComparison.Ordinal);

        // A failed row keeps the quarantine text as well: the warning follows the error rather
        // than replacing it.
        Assert.Contains("moved aside", row.Message);
    }

    [Fact]
    public void Warnings_survive_as_a_warning_message()
    {
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Completed, warnings: ["the archive was moved aside"]));

        Assert.Equal("Installed", row.StatusText);
        Assert.Equal("the archive was moved aside", row.Message);
        Assert.Equal(RowMessageKind.Warning, row.MessageKind);
    }

    [Fact]
    public void The_controls_follow_the_queue_state()
    {
        var row = Row();

        row.ApplyQueue(Snap(QueueItemState.Queued));
        Assert.True(row.CanCancel);
        Assert.True(row.CanRemove);

        row.ApplyQueue(Snap(QueueItemState.Downloading));
        Assert.True(row.CanCancel);
        Assert.False(row.CanRemove);

        row.ApplyQueue(Snap(QueueItemState.Failed));
        Assert.False(row.CanCancel);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public void The_controls_reach_the_queue_method_they_name()
    {
        // Without this, CancelCommand bound to _queue.Remove (or the two swapped) passes every
        // CanExecute assertion above. IQueueController exists so that these five methods are the
        // application's only contact with core, so nothing else in the shell would notice the
        // mix-up.
        var (row, queue) = RowWithQueue();
        row.ApplyQueue(Snap(QueueItemState.Queued));

        row.CancelCommand.Execute(null);
        row.RemoveCommand.Execute(null);

        Assert.Equal(["Cancel:EP01", "Remove:EP01"], queue.Calls);
    }

    [Fact]
    public void A_scan_clears_a_terminal_overlay_but_keeps_its_message()
    {
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Failed, error: "the mirror returned 404"));
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));

        Assert.Null(row.QueueState);
        Assert.Equal("Not installed", row.StatusText);
        Assert.Equal("the mirror returned 404", row.Message);
        Assert.True(row.IsCheckable);
    }

    [Fact]
    public void A_scan_does_not_clear_a_running_overlay()
    {
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Downloading, 500, 1000));
        row.ApplyScan(Scan(PackInstallState.Installed));

        Assert.Equal(QueueItemState.Downloading, row.QueueState);
        Assert.Equal("Downloading  50%", row.StatusText);
    }

    [Fact]
    public void A_republished_terminal_snapshot_does_not_resurrect_a_retired_overlay()
    {
        // A real PackQueue re-publishes every item wholesale when ITS OWN state moves (Running
        // to Idle once nothing is left to run), not only when an item's own state changes, so
        // a terminal item's snapshot echoes at least once after the scan already retired it.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Completed));
        row.ApplyScan(Scan(PackInstallState.Installed));
        row.ApplyQueue(Snap(QueueItemState.Completed));   // the queue's idle-transition echo

        Assert.Null(row.QueueState);
        Assert.Equal("Installed", row.StatusText);
    }

    [Fact]
    public void A_re_enqueued_pack_reaching_the_same_ending_again_is_not_suppressed()
    {
        // The guard above must not become permanent: a genuine re-enqueue always passes
        // through a non-terminal state first (Reset publishes Queued), and that has to clear
        // it so a second, real completion is applied rather than swallowed as another echo.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Completed));
        row.ApplyScan(Scan(PackInstallState.Installed));
        row.ApplyQueue(Snap(QueueItemState.Queued));      // Reset publishes this first
        row.ApplyQueue(Snap(QueueItemState.Completed));

        Assert.Equal(QueueItemState.Completed, row.QueueState);
    }

    [Fact]
    public void Clearing_the_overlay_drops_the_message_too()
    {
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));
        row.ApplyQueue(Snap(QueueItemState.Failed, error: "the mirror returned 404"));
        row.ClearQueueOverlay();

        Assert.Null(row.QueueState);
        Assert.Null(row.Message);
        Assert.Equal("Not installed", row.StatusText);
    }

    // ---- feature detector 1 ----
    [Fact]
    public void A_finished_pack_stops_naming_the_last_file_it_extracted()
    {
        // QueueItemSnapshot.CurrentEntry is sticky by design: only PackQueue.Reset clears it,
        // so a Completed snapshot still carries the last entry the installer wrote. Asserting
        // an empty CurrentEntry alone would pass for a row that never had one, so this asserts
        // the terminal state too, and first proves the entry was populated.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Installing, 500, 1000, entry: "Data/Simulation.package"));

        Assert.Equal("Data/Simulation.package", row.CurrentEntry);

        row.ApplyQueue(Snap(QueueItemState.Completed, 1000, 1000, entry: "Data/Simulation.package"));

        Assert.Equal(QueueItemState.Completed, row.QueueState);
        Assert.Null(row.CurrentEntry);
    }

    [Fact]
    public void A_blocked_pack_can_be_checked_again_because_that_is_the_escape_core_offers()
    {
        // PackQueue.Enqueue accepts a re-enqueue of a Blocked item, and MarkBlocked's text
        // promises it on both the first block and the second. The checkbox is how the user takes
        // it, so leaving the row unavailable makes core's own instruction impossible to follow.
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));
        row.ApplyQueue(Snap(QueueItemState.Blocked, error: "Another copy is working on 'EP01'."));

        // The pair matters: checkable *while still showing as blocked*. Asserting IsCheckable
        // alone would pass if the overlay had simply been dropped.
        Assert.Equal(QueueItemState.Blocked, row.QueueState);
        Assert.True(row.IsCheckable);
    }

    [Fact]
    public void A_scan_retires_a_blocked_overlay_the_queue_has_given_up_on()
    {
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Blocked, error: "still busy", isFinal: true));
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));

        Assert.Null(row.QueueState);
        Assert.True(row.IsCheckable);
    }

    [Fact]
    public void A_scan_keeps_a_blocked_overlay_the_queue_will_still_retry_by_itself()
    {
        // The contrast that gives the test above its meaning: a first block is not terminal, so
        // the row must go on saying so rather than reading as untouched work.
        var row = Row();
        row.ApplyQueue(Snap(QueueItemState.Blocked, error: "will be retried", isFinal: false));
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));

        Assert.Equal(QueueItemState.Blocked, row.QueueState);
        Assert.Equal("will be retried", row.StatusText);
    }
}
