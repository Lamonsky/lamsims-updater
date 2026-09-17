using LamSims.Core.Queueing;
using System.Collections.Generic;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class KindFlagTests
{
    private static PackRowViewModel Row() =>
        new(Packs.Entry(), new RecordingQueue());

    private static QueueItemSnapshot Snapshot(
        QueueItemState state, string? error = null, params string[] warnings) =>
        new("EP01", "Get to Work", state, 0, 0, 0, null, null, error, warnings);

    [Fact]
    public void A_failed_row_is_an_error_and_not_a_warning()
    {
        var row = Row();
        row.ApplyQueue(Snapshot(QueueItemState.Failed, error: "the archive did not verify"));

        Assert.Equal(RowMessageKind.Error, row.MessageKind);
        Assert.True(row.IsError);
        Assert.False(row.IsWarning);
    }

    [Fact]
    public void A_completed_row_carrying_warnings_is_a_warning_and_not_an_error()
    {
        var row = Row();
        row.ApplyQueue(Snapshot(QueueItemState.Completed, warnings: "the archive was moved aside"));

        Assert.Equal(RowMessageKind.Warning, row.MessageKind);
        Assert.True(row.IsWarning);
        Assert.False(row.IsError);
    }

    [Fact]
    public void A_quiet_row_is_neither()
    {
        var row = Row();

        Assert.Equal(RowMessageKind.None, row.MessageKind);
        Assert.False(row.IsWarning);
        Assert.False(row.IsError);
    }

    /// <summary>
    /// The flags are useless to a selector unless a change is announced: a class binding is only
    /// re-evaluated on PropertyChanged. Asserting the values without asserting the notification
    /// would leave a row that transitions from warning to error keeping its old colour, which is
    /// exactly the defect this pair of properties exists to avoid.
    /// </summary>
    [Fact]
    public void Both_flags_are_announced_when_the_kind_changes()
    {
        var row = Row();
        row.ApplyQueue(Snapshot(QueueItemState.Completed, warnings: "the archive was moved aside"));

        var announced = new List<string>();
        row.PropertyChanged += (_, e) => announced.Add(e.PropertyName!);

        row.ApplyQueue(Snapshot(QueueItemState.Failed, error: "the archive did not verify"));

        Assert.Contains(nameof(PackRowViewModel.IsWarning), announced);
        Assert.Contains(nameof(PackRowViewModel.IsError), announced);
        Assert.True(row.IsError);
        Assert.False(row.IsWarning);
    }

    [Theory]
    [InlineData(BannerKind.Info, false, false)]
    [InlineData(BannerKind.Warning, true, false)]
    [InlineData(BannerKind.Error, false, true)]
    public void A_banner_reports_its_kind_as_flags(BannerKind kind, bool warning, bool error)
    {
        var banner = new Banner("id", "text", kind);

        Assert.Equal(warning, banner.IsWarning);
        Assert.Equal(error, banner.IsError);
    }
}
