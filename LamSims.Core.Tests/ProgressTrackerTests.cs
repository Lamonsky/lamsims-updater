using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class ProgressTrackerTests
{
    private static DownloadProgress At(long bytes) => new("EP01", bytes, 1_000_000, 0, null);

    [Fact]
    public void Never_hands_the_caller_a_value_lower_than_one_it_already_handed()
    {
        const int Workers = 8;
        const int PerWorker = 20_000;
        const long Total = Workers * PerWorker;

        var tracker = new ProgressTracker("EP01", Total);
        var next = 0L;

        var highWater = -1L;
        var inversions = 0;
        var gate = new object();

        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            lock (gate)
            {
                if (p.BytesCompleted < highWater) inversions++;
                else highWater = p.BytesCompleted;
            }
        });

        // Eight workers is the engine's default connection count, and this is the interleaving
        // that matters: two workers passing the ordering check in one order and reaching the
        // caller's handler in the other.
        var workers = Enumerable.Range(0, Workers).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                var value = Interlocked.Increment(ref next);
                tracker.Deliver(progress, new DownloadProgress("EP01", value, Total, 0, null));
            }
        })).ToArray();

        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();

        Assert.Equal(0, inversions);
        Assert.Equal(Total, highWater);
    }

    [Fact]
    public void Leaves_an_arriving_worker_free_while_another_reports()
    {
        var tracker = new ProgressTracker("EP01", 1_000_000);
        using var reporterIsInTheHandler = new ManualResetEventSlim(false);
        using var releaseTheHandler = new ManualResetEventSlim(false);

        var gate = new object();
        var observed = new List<long>();
        var inHandler = 0;
        var mostAtOnce = 0;

        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            lock (gate)
            {
                inHandler++;
                if (inHandler > mostAtOnce) mostAtOnce = inHandler;
                observed.Add(p.BytesCompleted);
            }

            // The lock is released before parking, so a second worker entering the handler can
            // still record itself and a regression fails rather than deadlocks. The park also
            // outlasts the arriving worker's join deadline below by a wide margin, so a blocked
            // arriver cannot slip through on this wait expiring first.
            if (p.BytesCompleted == 100)
            {
                reporterIsInTheHandler.Set();
                releaseTheHandler.Wait(TimeSpan.FromSeconds(60));
            }

            lock (gate) inHandler--;
        });

        var reporter = new Thread(() => tracker.Deliver(progress, At(100)));
        reporter.Start();
        Assert.True(reporterIsInTheHandler.Wait(TimeSpan.FromSeconds(10)));

        var arriver = new Thread(() => tracker.Deliver(progress, At(200)));
        arriver.Start();

        // A worker must not wait on another worker's handler; reporting from inside the lock
        // would park this thread instead.
        var arriverWasFree = arriver.Join(TimeSpan.FromSeconds(3));

        releaseTheHandler.Set();
        Assert.True(reporter.Join(TimeSpan.FromSeconds(60)));
        Assert.True(arriver.Join(TimeSpan.FromSeconds(60)));
        Assert.True(arriverWasFree, "the arriving worker waited on the reporting worker's handler");

        lock (gate)
        {
            // One at a time, and the deposited snapshot still went out.
            Assert.Equal(1, mostAtOnce);
            Assert.Equal(new[] { 100L, 200L }, observed);
        }
    }

    [Fact]
    public void Delivers_the_newest_snapshot_however_two_workers_interleave()
    {
        // The newest snapshot must reach the caller whichever order the two workers interleave in.
        // The stand-down invariant's losing window, where a worker deposits between the reporter's
        // last check and its release of the flag, is a few instructions wide and 500 rounds do not
        // hit it; splitting the check and the clear across two lock acquisitions still passes.
        for (var round = 0; round < 500; round++)
        {
            var tracker = new ProgressTracker("EP01", 1_000_000);
            var gate = new object();
            var highest = 0L;

            var progress = new SyncProgress<DownloadProgress>(p =>
            {
                lock (gate)
                {
                    if (p.BytesCompleted > highest) highest = p.BytesCompleted;
                }
            });

            var first = new Thread(() => tracker.Deliver(progress, At(100)));
            var second = new Thread(() => tracker.Deliver(progress, At(200)));

            first.Start();
            second.Start();
            first.Join();
            second.Join();

            lock (gate) Assert.Equal(200, highest);
        }
    }

    [Fact]
    public void Drops_a_snapshot_already_overtaken_by_a_later_one()
    {
        var tracker = new ProgressTracker("EP01", 1_000_000);
        var observed = new List<long>();
        var progress = new SyncProgress<DownloadProgress>(p => observed.Add(p.BytesCompleted));

        tracker.Deliver(progress, At(200));
        tracker.Deliver(progress, At(100));

        Assert.Equal(new[] { 200L }, observed);
    }

    [Fact]
    public void Keeps_delivering_after_a_handler_throws()
    {
        var tracker = new ProgressTracker("EP01", 1_000_000);
        var observed = new List<long>();

        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.BytesCompleted == 100) throw new InvalidOperationException("bound to a dead view");
            observed.Add(p.BytesCompleted);
        });

        tracker.Deliver(progress, At(100));
        tracker.Deliver(progress, At(200));

        // Swallowing the fault is not enough: the tracker is unusable afterwards if the throw
        // leaves it marked as still reporting.
        Assert.Equal(new[] { 200L }, observed);
    }

    [Fact]
    public void Counts_bytes_from_an_already_completed_resume()
    {
        var tracker = new ProgressTracker("EP01", 1_000, alreadyCompletedBytes: 400);

        tracker.CommitChunk(0, 100);

        Assert.Equal(500, tracker.BytesCompleted);
        Assert.Equal(500, tracker.Snapshot().BytesCompleted);
    }

    [Fact]
    public void Provisional_bytes_count_toward_the_snapshot_before_a_chunk_completes()
    {
        var tracker = new ProgressTracker("EP01", 1000);

        tracker.Advance(worker: 0, bytes: 250);

        Assert.Equal(250, tracker.Snapshot().BytesCompleted);
    }

    [Fact]
    public void An_abandoned_attempt_gives_its_bytes_back()
    {
        var tracker = new ProgressTracker("EP01", 1000);

        tracker.Advance(worker: 0, bytes: 250);
        tracker.Abandon(worker: 0);

        Assert.Equal(0, tracker.Snapshot().BytesCompleted);
    }

    [Fact]
    public void Committing_a_chunk_clears_that_worker_provisional_bytes_in_the_same_breath()
    {
        var tracker = new ProgressTracker("EP01", 1000);

        tracker.Advance(worker: 0, bytes: 500);
        tracker.CommitChunk(worker: 0, bytes: 500);

        // 500, not 1000: the chunk must not be counted as both provisional and committed.
        Assert.Equal(500, tracker.Snapshot().BytesCompleted);
    }

    [Fact]
    public void Workers_provisional_bytes_are_kept_apart()
    {
        var tracker = new ProgressTracker("EP01", 1000);

        tracker.Advance(worker: 0, bytes: 100);
        tracker.Advance(worker: 1, bytes: 200);
        tracker.Abandon(worker: 0);

        Assert.Equal(200, tracker.Snapshot().BytesCompleted);
    }

    [Fact]
    public void A_snapshot_taken_while_another_worker_commits_never_exceeds_the_total()
    {
        // Deliver latches _lastAccepted monotonically, so one snapshot above the total suppresses
        // the true final one for good.
        //
        // The window is a couple of instructions wide between CommitChunk's two updates, and a
        // single 8-worker burst lands inside it about half the time. Raising the worker count
        // oversubscribes the cores and starves the observer into coarse scheduling slices, so the
        // burst is repeated 30 times instead: at a ~50% catch rate no run of a buggy build misses
        // every one.
        for (var round = 0; round < 30; round++)
        {
            const int Workers = 8;
            const long Total = Workers * 1000;
            var tracker = new ProgressTracker("EP01", Total);
            var overshoots = 0;
            var committing = true;

            var committers = Enumerable.Range(0, Workers).Select(worker => new Thread(() =>
            {
                tracker.Advance(worker, 1000);
                tracker.CommitChunk(worker, 1000);
            })).ToArray();

            // Driven off a flag cleared after the committers finish, not a fixed iteration
            // count: an uncontended Snapshot() is cheap enough that a fixed count can finish
            // before the commit burst even starts, or long after it ends, and never sample the
            // interleaving it exists to catch.
            var observer = new Thread(() =>
            {
                while (Volatile.Read(ref committing))
                    if (tracker.Snapshot().BytesCompleted > Total) Interlocked.Increment(ref overshoots);
            });

            observer.Start();
            foreach (var t in committers) t.Start();
            foreach (var t in committers) t.Join();
            Volatile.Write(ref committing, false);
            observer.Join();

            Assert.Equal(0, overshoots);
        }
    }

    [Fact]
    public void Speed_never_goes_negative_when_an_attempt_is_abandoned()
    {
        // Drives the clock explicitly. Without it the 200ms speed window never elapses inside a
        // unit test, every Snapshot returns a speed of 0, and the test passes with the clamp
        // deleted.
        var now = TimeSpan.Zero;
        var tracker = new ProgressTracker("EP01", 10_000) { Clock = () => now };

        tracker.Advance(worker: 0, bytes: 5_000);
        now += TimeSpan.FromSeconds(1);
        Assert.True(tracker.Snapshot().BytesPerSecond > 0);

        tracker.Abandon(worker: 0);
        // Just past the minimum resample threshold rather than another full second: the instant
        // rate over ~210ms is about five times steeper than the 5,000 B/s baseline, so it
        // dominates the 0.3 smoothing blend, where a same-length window leaves the blend positive
        // even with the clamp deleted. 210ms rather than 200ms keeps clear of the resample gate's
        // `>=` boundary.
        now += TimeSpan.FromMilliseconds(210);

        Assert.True(tracker.Snapshot().BytesPerSecond >= 0);
    }

    [Fact]
    public void Speed_is_not_resampled_inside_the_window()
    {
        var now = TimeSpan.Zero;
        var tracker = new ProgressTracker("EP01", 10_000) { Clock = () => now };

        tracker.Advance(worker: 0, bytes: 1_000);
        now += TimeSpan.FromSeconds(1);
        var settled = tracker.Snapshot().BytesPerSecond;

        // A megabyte-granularity report 1ms later must not resample: the instant rate over a
        // sliver of a window is meaningless and would wreck the average.
        tracker.Advance(worker: 0, bytes: 1_000);
        now += TimeSpan.FromMilliseconds(1);

        Assert.Equal(settled, tracker.Snapshot().BytesPerSecond);
    }

    [Fact]
    public void A_snapshot_deposited_while_the_reporter_is_reporting_is_still_delivered()
    {
        var tracker = new ProgressTracker("EP01", 1000);
        var seen = new List<long>();
        var deposited = false;

        var progress = new SyncProgress<DownloadProgress>(p => seen.Add(p.BytesCompleted));

        tracker.BeforeReport = () =>
        {
            if (deposited) return;
            deposited = true;
            // Lands while _reporting is set: the reporter must pick it up, not stand down.
            tracker.Deliver(progress, new DownloadProgress("EP01", 900, 1000, 0, null));
        };

        tracker.Deliver(progress, new DownloadProgress("EP01", 100, 1000, 0, null));

        Assert.Equal(new[] { 100L, 900L }, seen);
    }
}
