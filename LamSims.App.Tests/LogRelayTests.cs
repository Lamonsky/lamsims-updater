using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.Services;
using LamSims.Core.Logging;

namespace LamSims.App.Tests;

public class LogRelayTests
{
    private static LogRelay Build(out FakeClock clock)
    {
        clock = new FakeClock();
        return new LogRelay(clock);
    }

    [Fact]
    public void A_line_is_stamped_with_the_clock_and_flattened_for_the_view()
    {
        var relay = Build(out var clock);
        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(4));

        // Derived from the clock rather than hardcoded: the relay renders LOCAL time, so a literal
        // "14:03:04" would pass only on a UTC machine and fail everywhere else. The format is
        // still pinned exactly.
        var expected = clock.UtcNow.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        relay.Write(LogLine.Warning("careful", "EP01"));

        Assert.True(relay.TryDequeue(out var entry));
        Assert.Equal(expected, entry.Time);
        Assert.Equal("EP01", entry.Code);
        Assert.Equal("careful", entry.Text);
        Assert.True(entry.IsWarning);
        Assert.False(entry.IsError);
    }

    /// <summary>
    /// Sixteen download connections may report at once. Losing or duplicating a line here would
    /// be invisible in the window and impossible to reproduce.
    /// </summary>
    [Fact]
    public async Task Every_line_written_concurrently_arrives_exactly_once()
    {
        var relay = Build(out _);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 200; n++) relay.Write(LogLine.Info($"{i}-{n}"));
        })));

        var seen = new List<string>();
        while (relay.TryDequeue(out var entry)) seen.Add(entry.Text);

        Assert.Equal(3200, seen.Count);
        Assert.Equal(3200, seen.Distinct().Count());
    }

    /// <summary>
    /// The pair that matters: a burst costs ONE wake, and a wake is available again once the
    /// consumer has drained. Asserting only the first would pass for a relay that never woke
    /// twice, which would freeze the log after its first burst.
    /// </summary>
    [Fact]
    public void A_burst_wakes_the_consumer_once_and_can_wake_again_after_a_drain()
    {
        var relay = Build(out _);
        var wakes = 0;
        relay.OnAvailable = () => Interlocked.Increment(ref wakes);

        for (var n = 0; n < 50; n++) relay.Write(LogLine.Info($"{n}"));
        Assert.Equal(1, wakes);

        while (relay.TryDequeue(out _)) { }
        relay.ReleaseWake();

        relay.Write(LogLine.Info("after"));
        Assert.Equal(2, wakes);
    }

    /// <summary>
    /// The relay is built with no dispatcher (see the class doc) and gains one only when a view
    /// model attaches later. A write in that window must not take the wake flag for itself and
    /// leave it stuck — the very next write, once a handler exists, must still be able to wake.
    /// </summary>
    [Fact]
    public void A_write_before_OnAvailable_is_attached_does_not_stall_the_next_wake()
    {
        var relay = Build(out _);

        relay.Write(LogLine.Info("before"));

        var wakes = 0;
        relay.OnAvailable = () => Interlocked.Increment(ref wakes);

        relay.Write(LogLine.Info("after"));
        Assert.Equal(1, wakes);
    }

    /// <summary>
    /// A write can race a drain and lose: it arrives while the flag is still held, so it raises no
    /// wake, and its line sits queued with nobody watching. ReleaseWake must notice that on its
    /// own — no later, unrelated write is required to surface it — or a line delivered at exactly
    /// this moment (the final "install complete", say) is never seen.
    /// </summary>
    [Fact]
    public void ReleaseWake_wakes_again_when_a_line_arrived_during_the_drain()
    {
        var relay = Build(out _);
        var wakes = 0;
        relay.OnAvailable = () => Interlocked.Increment(ref wakes);

        relay.Write(LogLine.Info("a"));
        Assert.Equal(1, wakes);

        Assert.True(relay.TryDequeue(out var first));
        Assert.Equal("a", first.Text);

        // Arrives while the flag is still held: no second wake yet.
        relay.Write(LogLine.Info("b"));
        Assert.Equal(1, wakes);

        relay.ReleaseWake();
        Assert.Equal(2, wakes);

        Assert.True(relay.TryDequeue(out var second));
        Assert.Equal("b", second.Text);
    }

    /// <summary>
    /// The handler is `dispatcher.Post(Drain)` in production, and a shut-down dispatcher can throw
    /// during exit. ILogSink must never break the caller that logged through it, so the throw must
    /// not escape Write — and the flag must not stay taken, or the throw reproduces a permanent
    /// stall once a working handler is attached afterward.
    /// </summary>
    [Fact]
    public void A_throwing_handler_does_not_escape_Write_or_leave_the_flag_stuck()
    {
        var relay = Build(out _);
        var attempts = 0;
        relay.OnAvailable = () =>
        {
            attempts++;
            throw new InvalidOperationException("dispatcher shut down");
        };

        var thrown = Record.Exception(() => relay.Write(LogLine.Info("x")));
        Assert.Null(thrown);
        Assert.Equal(1, attempts);

        var wakes = 0;
        relay.OnAvailable = () => Interlocked.Increment(ref wakes);

        relay.Write(LogLine.Info("y"));
        Assert.Equal(1, wakes);
    }
}
