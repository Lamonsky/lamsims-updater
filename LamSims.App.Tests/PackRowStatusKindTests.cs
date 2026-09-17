using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

/// <summary>
/// The colour behind the row's status. StatusText and StatusKind read the same states in the same
/// order, and these pin that they agree: a row that says "Failed" in green would be worse than a
/// row with no colour at all.
/// </summary>
public class PackRowStatusKindTests
{
    private static PackRowViewModel Row() => new(Packs.Entry(), new RecordingQueue());

    private static QueueItemSnapshot Snap(QueueItemState state) =>
        new("EP01", "Get to Work", state, 0, 0, 0, null, null, null, Array.Empty<string>());

    private static PackScanResult Scan(PackInstallState state, params string[] missing) =>
        new("EP01", state, missing, null, null);

    [Theory]
    [InlineData(PackInstallState.NotInstalled, PackStatusKind.Idle)]
    [InlineData(PackInstallState.Partial, PackStatusKind.Warning)]
    [InlineData(PackInstallState.InstalledUnverified, PackStatusKind.Warning)]
    [InlineData(PackInstallState.Installed, PackStatusKind.Ok)]
    public void A_scanned_row_takes_its_colour_from_the_install_state(
        PackInstallState state, PackStatusKind expected)
    {
        var row = Row();
        row.ApplyScan(Scan(state));

        Assert.Equal(expected, row.StatusKind);
    }

    [Theory]
    [InlineData(QueueItemState.Queued, PackStatusKind.Idle)]
    [InlineData(QueueItemState.Verifying, PackStatusKind.Busy)]
    [InlineData(QueueItemState.Downloading, PackStatusKind.Busy)]
    [InlineData(QueueItemState.Installing, PackStatusKind.Busy)]
    [InlineData(QueueItemState.Completed, PackStatusKind.Ok)]
    [InlineData(QueueItemState.Cancelled, PackStatusKind.Idle)]
    [InlineData(QueueItemState.Failed, PackStatusKind.Error)]
    [InlineData(QueueItemState.Blocked, PackStatusKind.Error)]
    public void A_queued_row_takes_its_colour_from_the_queue_state(
        QueueItemState state, PackStatusKind expected)
    {
        var row = Row();
        row.ApplyQueue(Snap(state));

        Assert.Equal(expected, row.StatusKind);
    }

    [Fact]
    public void The_queue_state_wins_over_the_scan_state()
    {
        // The scan says Installed, which is green. A re-install that then fails must not keep
        // reporting green: StatusText already prefers the queue here and the colour follows it.
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.Installed));
        row.ApplyQueue(Snap(QueueItemState.Failed));

        Assert.Equal("Failed", row.StatusText);
        Assert.Equal(PackStatusKind.Error, row.StatusKind);
    }

    [Fact]
    public void Every_state_lights_exactly_one_flag_or_none()
    {
        // The four bools are what the view binds; the enum is only a spelling of them. A state
        // that lit two would apply two Foreground setters and resolve by declaration order, and a
        // coloured state that lit none would be silently grey.
        var offenders = new List<string>();

        foreach (var state in Enum.GetValues<QueueItemState>())
        {
            var row = Row();
            row.ApplyQueue(Snap(state));
            var lit = Flags(row).Count(f => f);
            var wanted = row.StatusKind is PackStatusKind.Idle ? 0 : 1;

            if (lit != wanted) offenders.Add($"{state} is {row.StatusKind} and lights {lit} flag(s)");
        }

        foreach (var state in Enum.GetValues<PackInstallState>())
        {
            var row = Row();
            row.ApplyScan(Scan(state));
            var lit = Flags(row).Count(f => f);
            var wanted = row.StatusKind is PackStatusKind.Idle ? 0 : 1;

            if (lit != wanted) offenders.Add($"{state} is {row.StatusKind} and lights {lit} flag(s)");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static bool[] Flags(PackRowViewModel row) =>
        [row.IsStatusOk, row.IsStatusBusy, row.IsStatusWarning, row.IsStatusError];

    [Fact]
    public void An_idle_row_lights_no_flag_at_all()
    {
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.NotInstalled));

        Assert.Equal(PackStatusKind.Idle, row.StatusKind);
        Assert.False(row.IsStatusOk);
        Assert.False(row.IsStatusBusy);
        Assert.False(row.IsStatusWarning);
        Assert.False(row.IsStatusError);
    }

    [Fact]
    public void A_change_of_state_announces_the_colour_as_well_as_the_text()
    {
        // Without this the row's colour is right only until the first state change: the view binds
        // Classes to the four bools, and a bool nothing raises never re-evaluates.
        var row = Row();
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        row.ApplyQueue(Snap(QueueItemState.Downloading));

        Assert.Contains(nameof(PackRowViewModel.StatusKind), seen);
        Assert.Contains(nameof(PackRowViewModel.IsStatusOk), seen);
        Assert.Contains(nameof(PackRowViewModel.IsStatusBusy), seen);
        Assert.Contains(nameof(PackRowViewModel.IsStatusWarning), seen);
        Assert.Contains(nameof(PackRowViewModel.IsStatusError), seen);
    }
}
