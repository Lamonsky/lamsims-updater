using System;
using System.Linq;
using Xunit;
using LamSims.App.ViewModels;
using LamSims.Core.Queueing;

namespace LamSims.App.Tests;

public class ProgressTickerTests
{
    private static QueueItemSnapshot Downloading(long done, long total, double rate) =>
        new("EP01", "Get to Work", QueueItemState.Downloading, done, total, rate,
            null, null, null, Array.Empty<string>());

    [Fact]
    public void The_first_report_for_a_pack_is_always_logged()
    {
        var clock = new FakeClock();
        var sink = new RecordingLogSink();
        var ticker = new ProgressTicker(clock, sink);

        ticker.Observe(Downloading(120, 1000, 8_100_000));

        Assert.True(sink.Logged("12%"));
        Assert.True(sink.Logged("8.1 MB/s"));
    }

    /// <summary>
    /// The whole point: upstream logged a line per read. The pair asserts that a report inside
    /// the window is dropped AND that one past it is kept, because asserting only the drop would
    /// pass for a ticker that logged nothing ever again.
    /// </summary>
    [Fact]
    public void A_second_report_within_a_second_is_dropped_and_one_after_it_is_kept()
    {
        var clock = new FakeClock();
        var sink = new RecordingLogSink();
        var ticker = new ProgressTicker(clock, sink);

        ticker.Observe(Downloading(100, 1000, 1));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        ticker.Observe(Downloading(200, 1000, 1));

        Assert.Single(sink.Lines);

        clock.Advance(TimeSpan.FromMilliseconds(1000));
        ticker.Observe(Downloading(300, 1000, 1));

        Assert.Equal(2, sink.Lines.Count);
    }

    /// <summary>Two packs run one after another; one pack's ticker must not gag the other's.</summary>
    [Fact]
    public void Each_pack_carries_its_own_window()
    {
        var clock = new FakeClock();
        var sink = new RecordingLogSink();
        var ticker = new ProgressTicker(clock, sink);

        ticker.Observe(Downloading(100, 1000, 1));
        ticker.Observe(Downloading(100, 1000, 1) with { Code = "EP02" });

        Assert.Equal(2, sink.Lines.Count);
    }

    /// <summary>
    /// Only Downloading ticks. Installing reports entries, not bytes, and a percentage there
    /// would be a second progress story competing with the row's own.
    /// </summary>
    [Fact]
    public void A_state_that_is_not_downloading_never_ticks()
    {
        var clock = new FakeClock();
        var sink = new RecordingLogSink();
        var ticker = new ProgressTicker(clock, sink);

        ticker.Observe(Downloading(100, 1000, 1) with { State = QueueItemState.Installing });

        Assert.Empty(sink.Lines);
    }

    [Fact]
    public void A_pack_with_no_known_total_does_not_report_a_percentage()
    {
        var clock = new FakeClock();
        var sink = new RecordingLogSink();
        var ticker = new ProgressTicker(clock, sink);

        ticker.Observe(Downloading(100, 0, 1));

        Assert.Empty(sink.Lines);
    }
}
