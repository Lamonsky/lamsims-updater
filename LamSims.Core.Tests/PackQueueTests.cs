using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Threading.Channels;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Queueing;

namespace LamSims.Core.Tests;

/// <summary>
/// A runner whose every step the test drives. Each call parks until the test releases it, so
/// "the second pack did not start" is an assertion about state, not about elapsed time.
/// </summary>
internal sealed class ScriptedRunner : IPackRunner
{
    private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();

    internal sealed record Call(
        string Code,
        PackEntry Pack,
        string GameDirectory,
        IProgress<PackPhase>? Phase,
        IProgress<DownloadProgress>? Download,
        IProgress<InstallProgress>? Install,
        CancellationToken Token,
        TaskCompletionSource<PackWorkflowResult> Result);

    public async Task<Call> NextCallAsync() => await _calls.Reader.ReadAsync();

    public bool HasPendingCall => _calls.Reader.Count > 0;

    /// <summary>
    /// When true (the default) the call completes as Cancelled the instant its token trips.
    /// Set false to model work that keeps going after cancellation is requested, as ZipInstaller
    /// does by checking its token once per entry, so a test can prove the queue waits for a stop.
    /// </summary>
    public bool AnswersOnCancellation { get; init; } = true;

    public async Task<PackWorkflowResult> RunAsync(
        PackEntry pack, string gameDirectory, IProgress<PackPhase>? phase,
        IProgress<DownloadProgress>? downloadProgress, IProgress<InstallProgress>? installProgress,
        CancellationToken ct)
    {
        var call = new Call(
            pack.Code, pack, gameDirectory, phase, downloadProgress, installProgress, ct,
            new TaskCompletionSource<PackWorkflowResult>(TaskCreationOptions.RunContinuationsAsynchronously));

        await _calls.Writer.WriteAsync(call, CancellationToken.None);

        if (!AnswersOnCancellation) return await call.Result.Task;

        using (ct.Register(() => call.Result.TrySetResult(Cancelled())))
            return await call.Result.Task;
    }

    internal static PackWorkflowResult Completed() => new(
        PackStage.Done, null,
        new InstallResult(InstallOutcome.Installed, 0, null, Array.Empty<string>()),
        Array.Empty<string>());

    internal static PackWorkflowResult Cancelled() => new(
        PackStage.Downloading, DownloadResult.Cancelled(), null, Array.Empty<string>());

    internal static PackWorkflowResult Failed(string error) => new(
        PackStage.Downloading, DownloadResult.Failed(error), null, Array.Empty<string>());
}

/// <summary>
/// Reads the queue's updates on a background task and lets a test wait on a *published state*
/// rather than on the clock. The queue's own updates are the only deterministic timeline a test
/// has: a deferred pause lands in <c>RunItemAsync</c>'s finally, which publishes <c>Paused</c>
/// before the loop takes another turn, so "wait until Paused is published" replaces "sleep and
/// hope". Waiters left unmatched when the channel closes are faulted rather than left hanging,
/// which turns a mutation that removes the awaited publish into a fast red instead of a 15 s
/// timeout.
/// </summary>
internal sealed class UpdateWatcher
{
    private readonly Lock _gate = new();
    private readonly List<QueueUpdate> _seen = new();
    private readonly List<(Func<QueueUpdate, bool> Match, TaskCompletionSource Signal)> _waiters = new();

    public UpdateWatcher(PackQueue queue)
    {
        Completion = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
            {
                lock (_gate)
                {
                    _seen.Add(update);

                    for (var i = _waiters.Count - 1; i >= 0; i--)
                        if (_waiters[i].Match(update))
                        {
                            _waiters[i].Signal.TrySetResult();
                            _waiters.RemoveAt(i);
                        }
                }
            }

            lock (_gate)
            {
                foreach (var waiter in _waiters)
                    waiter.Signal.TrySetException(new InvalidOperationException(
                        "The queue's updates ended before a matching update arrived."));

                _waiters.Clear();
            }
        });
    }

    /// <summary>Completes when the queue closes its channel.</summary>
    public Task Completion { get; }

    public QueueUpdate? Last
    {
        get { lock (_gate) return _seen.Count == 0 ? null : _seen[^1]; }
    }

    public IReadOnlyList<QueueUpdate> Seen
    {
        get { lock (_gate) return _seen.ToArray(); }
    }

    public Task WaitForAsync(Func<QueueUpdate, bool> match)
    {
        lock (_gate)
        {
            if (_seen.Any(match)) return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, signal));
            return signal.Task;
        }
    }

    /// <summary>
    /// Every state one pack passed through, adjacent duplicates collapsed. Distinct() would also
    /// let through an older state redelivered after a newer one.
    /// </summary>
    public IReadOnlyList<QueueItemState> ItemStates(string code)
    {
        var states = Seen
            .SelectMany(u => u.Items.Where(i => i.Code == code))
            .Select(i => i.State)
            .ToArray();

        return states.Where((s, i) => i == 0 || s != states[i - 1]).ToArray();
    }
}

public class PackQueueTests
{
    private static PackEntry Pack(string code) => new(
        code, $"The Sims 4 {code}", PackType.Expansion, 1000, null,
        new string('a', 64), new[] { new Uri("https://example.invalid/x.zip") }, new[] { code });

    private static PackQueue NewQueue(ScriptedRunner runner, TempDir temp) =>
        new(runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });

    // Every test in this file carries [Fact(Timeout = ...)]. The queue's failure mode is a wait
    // nobody satisfies: an unreleased semaphore, a channel nobody completes. Deleting
    // `_signal.Release()` from Enqueue wedges most of these tests rather than failing them, at
    // 600 s apiece, and the timeout is what turns that into a red test.
    [Fact(Timeout = 15000)]
    public async Task Runs_one_pack_at_a_time()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var first = await runner.NextCallAsync();
        Assert.Equal("EP01", first.Code);
        Assert.False(runner.HasPendingCall);   // EP02 has not been started

        first.Result.SetResult(ScriptedRunner.Completed());

        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        second.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_pack_does_not_stop_the_ones_behind_it()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Failed("mirror is down"));

        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        second.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Equal(QueueItemState.Failed, final.Items[0].State);
        Assert.Equal("mirror is down", final.Items[0].Error);
        Assert.Equal(QueueItemState.Completed, final.Items[1].State);
    }

    [Fact(Timeout = 15000)]
    public async Task Item_state_follows_the_phases_the_runner_reports()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var states = new List<QueueItemState>();
        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
                if (update.Items.Count > 0) states.Add(update.Items[0].State);
        });

        var run = queue.RunAsync(CancellationToken.None);
        queue.Enqueue(Pack("EP01"), temp.Path);

        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Verifying);
        call.Phase!.Report(PackPhase.Installing);
        call.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await reader;

        // Every update carries the whole queue, so one state legitimately repeats across
        // consecutive updates. Distinct() would also let through an older state redelivered after
        // a newer one; collapsing only neighbours still fails on that.
        Assert.Equal(
            new[] { QueueItemState.Queued, QueueItemState.Verifying, QueueItemState.Installing, QueueItemState.Completed },
            states.Where((s, i) => i == 0 || s != states[i - 1]).ToArray());
    }

    [Fact(Timeout = 15000)]
    public async Task The_game_directory_is_the_one_captured_at_enqueue()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        var first = Path.Combine(temp.Path, "drive-one");
        var second = Path.Combine(temp.Path, "drive-two");

        queue.Enqueue(Pack("EP01"), first);
        queue.Enqueue(Pack("EP02"), second);

        var a = await runner.NextCallAsync();
        Assert.Equal(first, a.GameDirectory);
        a.Result.SetResult(ScriptedRunner.Completed());

        var b = await runner.NextCallAsync();
        Assert.Equal(second, b.GameDirectory);
        b.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
    }

    [Fact(Timeout = 15000)]
    public async Task Enqueueing_a_code_that_is_already_waiting_is_ignored()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP01"), temp.Path);

        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();

        // A queue that failed to dedupe would run EP01 a second time and `await run` would block
        // forever on a call nobody answers. Answering everything that arrives turns that into the
        // item-count assertion below. With dedupe working there is no second call and this parks.
        _ = Task.Run(async () =>
        {
            while (true) (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());
        });

        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Single(final.Items);
    }

    [Fact(Timeout = 15000)]
    public async Task Progress_reaches_the_consumer_under_the_default_interval()
    {
        // Every other test sets ProgressInterval to zero, which takes the rate limiter out of
        // the picture entirely. Without this one, a rate limiter that suppresses *everything*
        // would pass the whole suite while shipping a queue whose byte counters never move.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions());   // default 100ms

        // DownloadSink writes item.BytesCompleted before it consults the rate limiter, so the
        // un-rate-limited Publish that Settle triggers a moment later carries 500 whether or not
        // any progress update was delivered. Only an update still reading Downloading can have
        // come from PublishProgress, so the state is captured alongside the count.
        var seen = new List<(QueueItemState State, long Bytes)>();
        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
                if (update.Items.Count > 0) seen.Add((update.Items[0].State, update.Items[0].BytesCompleted));
        });

        var run = queue.RunAsync(CancellationToken.None);
        queue.Enqueue(Pack("EP01"), temp.Path);

        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);
        call.Download!.Report(new DownloadProgress("EP01", 500, 1000, 0, null));
        call.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await reader;

        Assert.Contains((QueueItemState.Downloading, 500L), seen);
    }

    [Fact(Timeout = 15000)]
    public async Task Progress_inside_the_interval_is_suppressed()
    {
        // A limiter with a zero-effective interval passes everything else in this file. No clock
        // seam is needed: LastProgressTicks starts at 0 and TickCount64 is milliseconds since
        // boot, so the first report always publishes, and the second, issued immediately after on
        // the same thread, falls inside a 60-second interval and must be dropped. Nothing else
        // can manufacture a (Downloading, 200) update, since Settle moves the item to Completed
        // under the same lock before its own Publish.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path),
            new QueueOptions { ProgressInterval = TimeSpan.FromSeconds(60) });

        var seen = new List<(QueueItemState State, long Bytes)>();
        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
                if (update.Items.Count > 0) seen.Add((update.Items[0].State, update.Items[0].BytesCompleted));
        });

        var run = queue.RunAsync(CancellationToken.None);
        queue.Enqueue(Pack("EP01"), temp.Path);

        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);
        call.Download!.Report(new DownloadProgress("EP01", 100, 1000, 0, null));
        call.Download!.Report(new DownloadProgress("EP01", 200, 1000, 0, null));
        call.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await reader;

        Assert.Contains((QueueItemState.Downloading, 100L), seen);
        Assert.DoesNotContain((QueueItemState.Downloading, 200L), seen);
    }

    [Fact(Timeout = 15000)]
    public async Task A_download_root_that_cannot_be_created_still_closes_the_channel()
    {
        // RunAsync owns the channel's completion on every exit path, including the ones that
        // never reach the loop. Directory.CreateDirectory throws on a read-only root, a denied
        // permission, or, portably here, a plain file where a directory component should be. A
        // consumer already inside `await foreach (var u in queue.Updates)` must see its
        // enumeration end rather than wait forever.
        using var temp = new TempDir();
        var blocker = Path.Combine(temp.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(Path.Combine(blocker, "downloads")),
            new QueueOptions { ProgressInterval = TimeSpan.Zero });

        var drained = Task.Run(async () =>
        {
            await foreach (var _ in queue.Updates) { }
        });

        var failure = await Assert.ThrowsAnyAsync<IOException>(() => queue.RunAsync(CancellationToken.None));
        Assert.Contains(blocker, failure.Message);

        // Without the channel completion this never returns and only the [Fact(Timeout)] ends it.
        await drained;
    }

    [Fact(Timeout = 15000)]
    public async Task An_idle_queue_says_so_without_being_shut_down()
    {
        // The long-lived-shell shape: no Complete(), RunAsync running until its token trips.
        // RunAsync's finally cannot publish the Idle state, so the transition has to be published
        // where it happens, or a shell gating a spinner on QueueUpdate.State is told Running
        // indefinitely with every item terminal.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        using var cts = new CancellationTokenSource();

        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
                if (update.State == QueueState.Idle
                    && update.Items.Count > 0
                    && update.Items[0].State == QueueItemState.Completed)
                    idle.TrySetResult();
        });

        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(Pack("EP01"), temp.Path);

        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        // Awaited before anything shuts the queue down, so the finally's publish cannot be what
        // satisfies it. Idle-with-a-Completed-item also cannot be the pre-enqueue update, which
        // carries no items at all.
        await idle.Task;

        cts.Cancel();
        await run;
        await reader;
    }

    [Fact(Timeout = 15000)]
    public async Task A_runner_that_throws_on_cancellation_settles_as_cancelled()
    {
        // PackWorkflow is result-shaped for cancellation today, so this is latent. The runner has
        // its own linked token, and a pause that surfaced as a throw would become a permanent
        // Failed carrying the framework's string, skipping the requeue Settle decides on.
        using var temp = new TempDir();
        var runner = new ScriptedRunner { AnswersOnCancellation = false };
        await using var queue = NewQueue(runner, temp);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();

        cts.Cancel();
        call.Result.SetCanceled(cts.Token);

        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Equal(QueueItemState.Cancelled, final.Items[0].State);
        Assert.Null(final.Items[0].Error);   // never "The operation was canceled."
    }

    [Fact(Timeout = 15000)]
    public async Task A_runner_that_returns_no_result_fails_the_item()
    {
        // Left Queued, the item is re-selected by TakeNext on the next turn with every await
        // completing synchronously: a hot loop appending a QueueUpdate per iteration to an
        // unbounded channel. It would exhaust memory rather than fail, so the [Fact] timeout
        // is what ends it.
        using var temp = new TempDir();
        await using var queue = new PackQueue(
            new NullResultRunner(), new DownloadPaths(temp.Path),
            new QueueOptions { ProgressInterval = TimeSpan.Zero });

        var run = queue.RunAsync(CancellationToken.None);
        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Complete();
        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Equal(QueueItemState.Failed, final.Items[0].State);
        Assert.Equal("The runner returned no result.", final.Items[0].Error);
    }

    [Fact(Timeout = 15000)]
    public async Task Re_enqueueing_a_pack_runs_it_against_the_refreshed_entry()
    {
        // Re-enqueueing a terminal item is the retry, and "mirror is down" is what motivates it,
        // so by the time the user presses it the shell may have reloaded the catalog. Retrying
        // against the dead mirror and the stale digest would fetch nothing new.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
                if (update.Items.Count > 0 && update.Items[0].State == QueueItemState.Failed)
                    failed.TrySetResult();
        });

        var run = queue.RunAsync(CancellationToken.None);
        queue.Enqueue(Pack("EP01"), temp.Path);

        var first = await runner.NextCallAsync();
        first.Result.SetResult(ScriptedRunner.Failed("mirror is down"));

        // The re-enqueue only counts as a retry once the item is terminal; before that Enqueue
        // drops it as "already going to run".
        await failed.Task;

        var refreshed = new PackEntry(
            "EP01", "The Sims 4 EP01", PackType.Expansion, 2000, null,
            new string('b', 64), new[] { new Uri("https://mirror.invalid/fresh.zip") }, new[] { "EP01" });
        queue.Enqueue(refreshed, temp.Path);

        var second = await runner.NextCallAsync();
        Assert.Equal(refreshed.Urls, second.Pack.Urls);
        Assert.Equal(refreshed.Sha256, second.Pack.Sha256);
        Assert.Equal(refreshed.Size, second.Pack.Size);
        second.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await reader;
    }

    [Fact(Timeout = 15000)]
    public async Task A_pause_clears_progress_the_resumed_run_cannot_account_for()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);

        // Bytes read but not yet committed to the chunk sidecar. ChunkFetcher reports Abandoned
        // only for a validator mismatch or a retryable error, never for cancellation, and a
        // resumed SegmentedDownloader rebuilds its baseline from committed chunks alone, so
        // whatever the queue keeps here is progress the next run cannot account for.
        call.Download!.Report(new DownloadProgress("EP01", 80_000_000, 128_000_000, 0, null));
        await watcher.WaitForAsync(u => u.Items[0].BytesCompleted == 80_000_000);

        queue.Pause();
        await call.Result.Task;

        // An item reading Queued is also what the original enqueue published, and that update is
        // already in Seen, so waiting on it alone returns immediately with the wrong update. Only
        // the pause requeue publishes Queued while the queue itself reads Paused.
        await watcher.WaitForAsync(u =>
            u.State == QueueState.Paused && u.Items[0].State == QueueItemState.Queued);

        var paused = watcher.Seen
            .Last(u => u.State == QueueState.Paused && u.Items[0].State == QueueItemState.Queued)
            .Items[0];

        // BytesCompleted is monotonic as a caller observes it, resting on Deliver's latch, which
        // is per-tracker. A resumed run builds a new tracker, so carrying this figure across the
        // pause makes a caller watch it fall. The state is asserted with the bytes, since the
        // bytes alone would also hold for an item dropped from the queue altogether.
        Assert.Equal(QueueItemState.Queued, paused.State);
        Assert.Equal(0, paused.BytesCompleted);
        Assert.Equal(0, paused.TotalBytes);

        queue.Resume();
        var again = await runner.NextCallAsync();
        again.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await watcher.Completion;
    }

    [Fact(Timeout = 15000)]
    public async Task Pause_mid_download_stops_the_transfer_and_requeues_the_pack()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);

        queue.Pause();

        // The runner's token is cancelled, which its ct.Register turns into a Cancelled result.
        await call.Result.Task;

        queue.Resume();

        // The same pack is handed out again rather than being left cancelled.
        var again = await runner.NextCallAsync();
        Assert.Equal("EP01", again.Code);
        again.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await watcher.Completion;

        // "It ended Completed" alone would also hold for a queue that never paused; the Queued in
        // the middle is the requeue, and no Cancelled snapshot means Settle converted it under the
        // one lock.
        Assert.Equal(
            new[]
            {
                QueueItemState.Queued,
                QueueItemState.Downloading,
                QueueItemState.Queued,
                QueueItemState.Completed,
            },
            watcher.ItemStates("EP01"));
    }

    [Fact(Timeout = 15000)]
    public async Task Pause_mid_install_is_deferred_to_the_item_boundary()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Installing);

        queue.Pause();

        Assert.False(call.Token.IsCancellationRequested);   // the extract runs on
        await watcher.WaitForAsync(u => u.State == QueueState.Pausing);

        call.Result.SetResult(ScriptedRunner.Completed());

        // The deferred pause lands in RunItemAsync's finally, which publishes Paused, so the test
        // waits for that update rather than guessing how long the item boundary takes.
        await watcher.WaitForAsync(u => u.State == QueueState.Paused);

        // Only "EP02 never started" needs a window, because nothing is published when nothing
        // happens. It is paired with a state assertion so the sleep is not carrying the test:
        // without TakeNext's pause guard the loop publishes Running the moment it takes another
        // turn, and that update lands after the Paused one.
        await Task.Delay(50);
        Assert.False(runner.HasPendingCall);
        Assert.All(
            watcher.Seen.SkipWhile(u => u.State != QueueState.Paused),
            u => Assert.Equal(QueueState.Paused, u.State));

        queue.Resume();
        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        second.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await watcher.Completion;
    }

    [Fact(Timeout = 15000)]
    public async Task A_pause_driven_requeue_is_not_handed_straight_back_out()
    {
        // A pause is the only thing that legitimately returns an item to Queued, so it needs a
        // guard against "an iteration ran, consumed no work, and the item is still Queued".
        // TakeNext's _pauseRequested check is that guard; without it the pack the user just
        // paused is handed straight back to the runner.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);

        queue.Pause();
        await call.Result.Task;

        // The pause has fully landed: the pack is back in the queue and the queue is Paused.
        await watcher.WaitForAsync(u =>
            u.State == QueueState.Paused
            && u.Items.Count == 1
            && u.Items[0].State == QueueItemState.Queued);

        await Task.Delay(50);
        Assert.False(runner.HasPendingCall);
        Assert.All(
            watcher.Seen.SkipWhile(u => u.State != QueueState.Paused),
            u => Assert.Equal(QueueState.Paused, u.State));

        queue.Resume();
        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await watcher.Completion;
    }

    [Fact(Timeout = 15000)]
    public async Task Resume_during_pausing_cancels_the_pending_pause()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Installing);

        queue.Pause();
        queue.Resume();

        Assert.False(call.Token.IsCancellationRequested);   // the extract was never interrupted
        call.Result.SetResult(ScriptedRunner.Completed());

        // Without the Pausing -> Running edge the queue halts here and this never arrives.
        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        second.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Equal(QueueState.Idle, final.State);
        Assert.All(final.Items, i => Assert.Equal(QueueItemState.Completed, i.State));
    }

    [Fact(Timeout = 15000)]
    public async Task Pause_while_idle_holds_a_later_enqueue()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Pause();
        queue.Enqueue(Pack("EP01"), temp.Path);

        // Enqueue publishes, so the test waits for the pack to be visible with the queue still
        // reading Paused rather than sleeping to let the enqueue land.
        await watcher.WaitForAsync(u =>
            u.State == QueueState.Paused
            && u.Items.Count == 1
            && u.Items[0].State == QueueItemState.Queued);

        // The residual "and it did not start" half keeps a bounded window, paired with the state
        // assertion below.
        await Task.Delay(50);
        Assert.False(runner.HasPendingCall);
        Assert.All(
            watcher.Seen.SkipWhile(u => u.Items.Count == 0),
            u => Assert.Equal(QueueState.Paused, u.State));

        queue.Resume();
        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await watcher.Completion;
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_the_active_item_leaves_the_queued_ones_running()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);
        queue.Cancel("EP01");
        await call.Result.Task;

        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        second.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;

        // "EP02 ran next" alone would also hold for a cancel that requeued EP01 and lost the race;
        // only Cancelled-and-still-listed says the cancel ended it. Cancel and Pause share a code
        // path down to the single PausedOut flag, and this is what separates them.
        var final = await LastUpdateAsync(queue);
        Assert.Equal(QueueItemState.Cancelled, final.Items[0].State);
        Assert.Equal(QueueItemState.Completed, final.Items[1].State);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_a_pending_pack_ends_it_without_touching_the_running_one()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();
        Assert.Equal("EP01", call.Code);

        queue.Cancel("EP02");

        Assert.False(call.Token.IsCancellationRequested);   // the running pack is untouched
        call.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Equal(QueueItemState.Completed, final.Items[0].State);
        Assert.Equal(QueueItemState.Cancelled, final.Items[1].State);
    }

    [Fact(Timeout = 15000)]
    public async Task Remove_takes_a_queued_item_out_of_the_list_but_refuses_the_active_one()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();

        Assert.False(queue.Remove("EP01"));    // active
        Assert.True(queue.Remove("EP02"));     // queued

        call.Result.SetResult(ScriptedRunner.Completed());
        queue.Complete();
        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Single(final.Items);
        Assert.Equal("EP01", final.Items[0].Code);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancel_all_leaves_cancelled_items_visible()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();
        queue.CancelAll();
        await call.Result.Task;

        queue.Complete();
        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Equal(2, final.Items.Count);
        Assert.All(final.Items, i => Assert.Equal(QueueItemState.Cancelled, i.State));
    }

    [Fact(Timeout = 15000)]
    public async Task The_controls_leave_a_terminal_pack_alone()
    {
        // A finished pack stays in the list so the user can see and retry it, so Remove must
        // refuse it and both cancels must skip it. A Completed pack flipped to Cancelled by a
        // later Cancel All would misreport work that actually happened.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        await watcher.WaitForAsync(u => u.Items.Count > 0 && u.Items[0].State == QueueItemState.Completed);

        Assert.False(queue.Remove("EP01"));
        Assert.False(queue.Remove("NOPE"));    // and an unknown code is not an error either
        queue.Cancel("EP01");
        queue.CancelAll();

        queue.Complete();
        await run;
        await watcher.Completion;

        var final = watcher.Last!;
        Assert.Single(final.Items);
        Assert.Equal(QueueItemState.Completed, final.Items[0].State);
    }

    [Fact(Timeout = 15000)]
    public async Task Re_enqueueing_a_failed_pack_runs_it_again_with_its_error_cleared()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Failed("mirror is down"));

        // The re-enqueue only counts as a retry once the item is terminal; before Settle runs,
        // Enqueue drops it as "already going to run".
        await watcher.WaitForAsync(u => u.Items.Count > 0 && u.Items[0].State == QueueItemState.Failed);

        queue.Enqueue(Pack("EP01"), temp.Path);
        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;
        await watcher.Completion;

        var final = watcher.Last!;
        Assert.Single(final.Items);
        Assert.Equal(QueueItemState.Completed, final.Items[0].State);
        Assert.Null(final.Items[0].Error);
    }

    [Fact(Timeout = 15000)]
    public async Task A_pause_landing_during_a_cancel_leaves_the_pack_cancelled()
    {
        // The window is the whole runner unwind, which under the real workflow is eight in-flight
        // range requests aborting plus a sidecar save, so a shell that auto-pauses on network loss
        // reaches it without the user touching anything. A Pause that re-stamped PausedOut on the
        // item the cancel had cleared would have Settle read the cancel as a requeue, and the pack
        // would come back Queued on the next Resume. AnswersOnCancellation is false so the unwind
        // is held open rather than raced.
        using var temp = new TempDir();
        var runner = new ScriptedRunner { AnswersOnCancellation = false };
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);

        queue.Cancel("EP01");
        Assert.True(call.Token.IsCancellationRequested);

        queue.Pause();

        // The runner finishes unwinding and reports the cancellation it was asked for.
        call.Result.SetResult(ScriptedRunner.Cancelled());

        await watcher.WaitForAsync(u => u.State == QueueState.Paused);
        Assert.Equal(QueueItemState.Cancelled, watcher.Last!.Items[0].State);

        // And it must stay ended: a requeued EP01 is handed straight back to the runner here.
        queue.Resume();
        await Task.Delay(50);
        Assert.False(runner.HasPendingCall);

        queue.Complete();
        await run;
        await watcher.Completion;

        Assert.Equal(QueueItemState.Cancelled, watcher.Last!.Items[0].State);
    }

    [Fact(Timeout = 15000)]
    public async Task A_pause_landing_during_a_cancel_all_leaves_the_pack_cancelled()
    {
        // The same path through CancelAll, the button a shell wires to "stop everything", whose
        // single active pack a following auto-pause would resurrect.
        using var temp = new TempDir();
        var runner = new ScriptedRunner { AnswersOnCancellation = false };
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);

        queue.CancelAll();
        Assert.True(call.Token.IsCancellationRequested);

        queue.Pause();
        call.Result.SetResult(ScriptedRunner.Cancelled());

        await watcher.WaitForAsync(u => u.State == QueueState.Paused);
        Assert.Equal(QueueItemState.Cancelled, watcher.Last!.Items[0].State);

        queue.Resume();
        await Task.Delay(50);
        Assert.False(runner.HasPendingCall);

        queue.Complete();
        await run;
        await watcher.Completion;

        Assert.Equal(QueueItemState.Cancelled, watcher.Last!.Items[0].State);
    }

    [Fact(Timeout = 15000)]
    public async Task A_control_does_not_hold_the_queue_lock_while_it_trips_the_token()
    {
        // CancellationTokenSource.Cancel runs every registered callback synchronously on the
        // calling thread. Under the real workflow that is a linked failure source, one deadline
        // source per in-flight range request with SocketsHttpHandler's connection teardown on
        // each, plus the per-read deadlines and timer registrations, all serial. Under _gate it
        // blocks every progress sink, Publish, Settle and every other control for the duration.
        //
        // The second thread is needed because _gate is a System.Threading.Lock the cancelling
        // thread already owns, so a callback re-entering the queue on that thread would sail
        // through either way.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();
        Assert.Equal("EP01", call.Code);

        var callbackRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlReturned = new SemaphoreSlim(0);
        var removed = false;
        var reachedTheQueue = false;

        var other = Task.Run(async () =>
        {
            await callbackRunning.Task;
            removed = queue.Remove("EP02");   // needs _gate
            controlReturned.Release();
        });

        using var registration = call.Token.Register(() =>
        {
            callbackRunning.TrySetResult();

            // Bounded, so a regression is a red assertion rather than a hung run: with Cancel
            // called under _gate the other thread cannot get into Remove until this callback, and
            // every other one the real workflow registers, has finished.
            reachedTheQueue = controlReturned.Wait(TimeSpan.FromSeconds(3));
        });

        queue.Cancel("EP01");

        await other;
        Assert.True(reachedTheQueue);
        Assert.True(removed);

        await call.Result.Task;
        queue.Complete();
        await run;
    }

    [Fact(Timeout = 15000)]
    public async Task A_pause_after_complete_is_refused_so_the_queue_still_drains()
    {
        // The reciprocal of Complete() clearing a standing pause. A pause accepted after "no more
        // work is coming" parks the loop on its signal with queued work and nothing to release it:
        // Enqueue is refused once _completed is set, and a shell on its way out sends no Resume,
        // so RunAsync never returns and every `await foreach` consumer waits on an open channel.
        //
        // With the pause refused there is no reachable way to park a completed queue. Finished()
        // is false only while something is Queued or once-blocked, RequeueBlocked requeues the
        // latter as soon as _completed is set, and TakeNext hands out the former unless the queue
        // is paused, which leaves CancelAll's _signal.Release() as defence in depth.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);

        queue.Complete();
        queue.Pause();

        // Pause trips the active run's token before it returns, so this is a fast red rather than
        // a wait nobody satisfies: an accepted pause is visible here, in the same statement order.
        Assert.False(call.Token.IsCancellationRequested);
        call.Result.SetResult(ScriptedRunner.Completed());

        // And the queue goes on to finish the work it was told to finish.
        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        second.Result.SetResult(ScriptedRunner.Completed());

        await run;
        await watcher.Completion;

        Assert.Equal(QueueState.Idle, watcher.Last!.State);
        Assert.All(watcher.Last!.Items, i => Assert.Equal(QueueItemState.Completed, i.State));
    }

    [Fact(Timeout = 15000)]
    public async Task Complete_while_paused_still_finishes_the_queued_work()
    {
        // "No more work is coming" has to mean the queue ends, and a pause left standing turns it
        // into the opposite: the loop parks on its signal forever, RunAsync never returns, its
        // finally never runs, and every `await foreach` consumer waits on a channel nobody
        // closes. Clearing the pause also settles what Complete means for work already queued:
        // it runs. "Finish what is queued and stop" is the whole contract, and a pack sitting
        // Queued because a pause cancelled it out is still work the user asked for.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = NewQueue(runner, temp);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Pause();
        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Complete();

        // No Resume anywhere in this test: a loop that stayed paused would hang here.
        var call = await runner.NextCallAsync();
        Assert.Equal("EP01", call.Code);
        call.Result.SetResult(ScriptedRunner.Completed());

        await run;

        var final = await LastUpdateAsync(queue);
        Assert.Equal(QueueState.Idle, final.State);
        Assert.Equal(QueueItemState.Completed, final.Items[0].State);
    }

    [Fact(Timeout = 15000)]
    public async Task An_item_whose_lock_is_held_is_blocked_retried_once_and_then_terminal()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        // Held for the whole run, so both attempts are refused. The lock file is left on disk when
        // the lock is released, so its existence says nothing about whether the lock is held;
        // TryAcquire returning null is the only signal of contention.
        using var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Complete();
        await run;
        await watcher.Completion;

        Assert.False(runner.HasPendingCall);   // the runner was never invoked

        // "It ended Blocked" alone would also hold for a queue that refused the pack once and gave
        // up; the second Queued is the drain retry, and the Blocked after it makes the item
        // terminal.
        Assert.Equal(
            new[]
            {
                QueueItemState.Queued,
                QueueItemState.Blocked,
                QueueItemState.Queued,
                QueueItemState.Blocked,
            },
            watcher.ItemStates("EP01"));

        var final = watcher.Last!;
        Assert.Equal(QueueItemState.Blocked, final.Items[0].State);
        Assert.NotNull(final.Items[0].Error);

        // Twice blocked is terminal: the user keeps it in view to read that error and queue it
        // again once the other copy has finished, so Remove refuses it as it refuses a Failed one.
        Assert.False(queue.Remove("EP01"));

        // A consumer cannot read an attempt count, so the snapshot has to say which block it is
        // looking at: the first is work still owed, the second is the queue standing down.
        // Asserting only the last would also hold for a field that was true on both.
        var blocked = watcher.Seen
            .SelectMany(u => u.Items.Where(i => i.Code == "EP01" && i.State == QueueItemState.Blocked))
            .ToArray();

        Assert.False(blocked[0].IsFinal);
        Assert.True(blocked[^1].IsFinal);
    }

    [Fact(Timeout = 15000)]
    public async Task A_blocked_item_runs_when_the_lock_is_free_by_the_retry_pass()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        // EP02 runs while EP01 is blocked; releasing before the drain lets the retry succeed.
        // "EP02 came first" is half the assertion: a queue that hung on to a refused pack, or one
        // that retried it the instant it was refused, would hand EP01 back here instead.
        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        holder!.Dispose();
        second.Result.SetResult(ScriptedRunner.Completed());

        // Complete() is not called until the retry has been handed out. The retry guard is a
        // disjunction of "another pack has finished since" and "no more work is coming", so
        // calling Complete() here would satisfy both and leave the _completedCount half unpinned,
        // where `if (!_completed) continue;` is a queue that never retries a blocked pack in a
        // long-lived shell. Every await below is on a runner call, so EP02's result is what moves
        // _completedCount from 0 to 1 and only that can produce this third call.
        var retried = await runner.NextCallAsync();
        Assert.Equal("EP01", retried.Code);
        retried.Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;

        var final = await LastUpdateAsync(queue);
        Assert.All(final.Items, i => Assert.Equal(QueueItemState.Completed, i.State));
    }

    [Fact(Timeout = 15000)]
    public async Task Cancel_all_stops_a_blocked_item_from_being_retried()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        using var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);

        // Let the first attempt be refused before cancelling. The Blocked update is published by
        // RunItemAsync's finally, which has already cleared _activeItem, so the cancel below takes
        // the pending path, the one that has to skip a blocked pack rather than treat it as ended.
        await watcher.WaitForAsync(u => u.Items.Count > 0 && u.Items[0].State == QueueItemState.Blocked);

        queue.CancelAll();
        queue.Complete();
        await run;
        await watcher.Completion;

        Assert.False(runner.HasPendingCall);

        var final = watcher.Last!;
        Assert.Equal(QueueItemState.Cancelled, final.Items[0].State);
    }

    [Fact(Timeout = 15000)]
    public async Task Remove_takes_out_a_blocked_pack_that_is_still_owed_its_retry()
    {
        // A once-blocked pack has not ended; it is waiting for the queue to drain, so Remove must
        // take it out as it takes out a Queued one. Classifying it as terminal would leave the
        // user unable to get rid of a pack that is still going to run.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        using var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        await watcher.WaitForAsync(u => u.Items.Count > 0 && u.Items[0].State == QueueItemState.Blocked);

        Assert.True(queue.Remove("EP01"));

        queue.Complete();
        await run;
        await watcher.Completion;

        Assert.Empty(watcher.Last!.Items);
    }

    [Fact(Timeout = 15000)]
    public async Task An_acquire_that_throws_fails_the_pack_instead_of_stranding_the_queue()
    {
        // TryAcquire returns null for contention and throws for everything else: a missing root, a
        // permission that changed mid-run, or, portably here, a directory sitting where the lock
        // file should be. The queue catches that and marks the item Failed with the message.
        // Acquired above the try it would skip the deregistration finally, leaving _activeItem
        // naming a retired run, so Remove refuses the pack forever and Pause never reaches Paused.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.LockFile("EP01"));

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        // Answers whatever the queue asks for. Awaiting EP02's call directly would wait forever on
        // a dead loop that never makes the call; this way `await run` rethrows the fault at once.
        _ = Task.Run(async () =>
        {
            while (true) (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());
        });

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);
        queue.Complete();

        await run;                 // RunAsync does not fault, and the queue reaches EP02
        await watcher.Completion;

        var final = watcher.Last!;
        Assert.Equal(QueueItemState.Failed, final.Items[0].State);
        Assert.Contains("EP01.lock", final.Items[0].Error);
        Assert.Equal(QueueItemState.Completed, final.Items[1].State);
    }

    [Fact(Timeout = 15000)]
    public async Task Re_enqueueing_a_blocked_pack_retries_it_at_once()
    {
        // In a long-lived shell there is no Complete(), so a lone blocked pack waits on a drain
        // that never comes, while its error says it will be retried once the rest of the queue has
        // run and the rest of the queue already has. Re-enqueueing is the one action that escapes
        // that, so Enqueue must not classify it as pending and return from inside the lock.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);      // no Complete(): nothing will ever drain

        queue.Enqueue(Pack("EP01"), temp.Path);
        await watcher.WaitForAsync(u => u.Items.Count > 0 && u.Items[0].State == QueueItemState.Blocked);

        holder!.Dispose();
        queue.Enqueue(Pack("EP01"), temp.Path);

        // An Enqueue that did nothing here would leave the [Fact(Timeout)] to end the test.
        var call = await runner.NextCallAsync();
        Assert.Equal("EP01", call.Code);
        call.Result.SetResult(ScriptedRunner.Completed());

        await watcher.WaitForAsync(u => u.Items[0].State == QueueItemState.Completed);
        cts.Cancel();
        await run;
        await watcher.Completion;

        Assert.Equal(
            new[]
            {
                QueueItemState.Queued,
                QueueItemState.Blocked,
                QueueItemState.Queued,
                QueueItemState.Completed,
            },
            watcher.ItemStates("EP01"));

        Assert.Null(watcher.Last!.Items[0].Error);   // Reset cleared the block's message
    }

    [Fact(Timeout = 15000)]
    public async Task Acquiring_the_lock_earns_a_blocked_pack_a_fresh_free_retry()
    {
        // The one free retry is per contiguous run of refusals, not per item lifetime. EP01 is
        // refused once, wins the lock on its retry, and is then put back in the queue by a pause.
        // If the spent attempt survives that, the next block is its second, so the pack goes
        // terminal carrying an error about a retry unrelated to the block the user is looking at.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        // EP01 is refused; EP02 finishing is what earns it the retry.
        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);
        holder!.Dispose();
        second.Result.SetResult(ScriptedRunner.Completed());

        // The retry acquires the lock, which is the successful acquisition whose whole job here is
        // to wipe the blocked history.
        var retried = await runner.NextCallAsync();
        Assert.Equal("EP01", retried.Code);
        retried.Phase!.Report(PackPhase.Downloading);

        // The pause returns it to Queued, and the other copy takes the lock before it resumes. No
        // race: RunItemAsync releases the lock before it publishes Paused, so waiting on that
        // update is a happens-after edge for "the lock is free again".
        queue.Pause();
        await retried.Result.Task;
        await watcher.WaitForAsync(u =>
            u.State == QueueState.Paused
            && u.Items.Count == 2
            && u.Items[0].State == QueueItemState.Queued);

        var again = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(again);

        // Enqueued while paused, and the reason the rest of this test has no race in it. Waiting on
        // a *published* second Blocked cannot work: the update carrying "EP01 Blocked, EP02
        // Completed" was already published when EP02 settled, so the waiter matches history and the
        // test races the loop to the lock. EP03 sits behind EP01 in the list, so the loop reaching
        // it can only mean EP01 was refused first.
        queue.Enqueue(Pack("EP03"), temp.Path);
        queue.Resume();

        var third = await runner.NextCallAsync();
        Assert.Equal("EP03", third.Code);

        // Released before EP03 finishes, because EP03 finishing is what earns EP01 the retry; this
        // second round is paid for by _completedCount moving rather than by Complete().
        again!.Dispose();
        third.Result.SetResult(ScriptedRunner.Completed());

        // Answers whatever the queue asks for next, so a pack wrongly left terminal ends the run
        // instead of hanging it: the state path below is then a red rather than a [Fact] timeout.
        _ = Task.Run(async () =>
        {
            while (true) (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());
        });

        queue.Complete();
        await run;
        await watcher.Completion;

        Assert.Equal(
            new[]
            {
                QueueItemState.Queued,
                QueueItemState.Blocked,
                QueueItemState.Queued,
                QueueItemState.Downloading,
                QueueItemState.Queued,
                QueueItemState.Blocked,
                QueueItemState.Queued,
                QueueItemState.Completed,
            },
            watcher.ItemStates("EP01"));
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_the_run_token_runs_no_retry_pass()
    {
        // The retry is owed to a queue that drains, not to one being shut down: a cancelled
        // RunAsync stops where it stands and a refused pack stays refused at one attempt.
        //
        // The cancel races the loop's own idle branch, so as a mutation detector this only bites
        // when the loop gets to RequeueBlocked first. Either interleaving must still refuse a
        // second attempt.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        using var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);      // no Complete()

        queue.Enqueue(Pack("EP01"), temp.Path);
        await watcher.WaitForAsync(u => u.Items.Count > 0 && u.Items[0].State == QueueItemState.Blocked);

        cts.Cancel();
        await run;
        await watcher.Completion;

        Assert.False(runner.HasPendingCall);
        Assert.Equal(
            new[] { QueueItemState.Queued, QueueItemState.Blocked },
            watcher.ItemStates("EP01"));
    }

    /// <summary>A runner that hands back nothing at all, which the interface's type does not forbid.</summary>
    private sealed class NullResultRunner : IPackRunner
    {
        public Task<PackWorkflowResult> RunAsync(
            PackEntry pack, string gameDirectory, IProgress<PackPhase>? phase,
            IProgress<DownloadProgress>? downloadProgress, IProgress<InstallProgress>? installProgress,
            CancellationToken ct) => Task.FromResult<PackWorkflowResult>(null!);
    }

    /// <summary>Drains the channel and returns the last update, which carries the final state.</summary>
    private static async Task<QueueUpdate> LastUpdateAsync(PackQueue queue)
    {
        QueueUpdate? last = null;
        await foreach (var update in queue.Updates) last = update;
        return last!;
    }
}
