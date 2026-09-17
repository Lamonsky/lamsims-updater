using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Concurrent;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Queueing;

namespace LamSims.Core.Tests;

/// <summary>
/// What disposal, a cancelled run token and a second RunAsync do to a queue's lock, channel and
/// loop.
///
/// Every test here carries <c>[Fact(Timeout = ...)]</c>, as in <see cref="PackQueueTests"/>: a
/// queue mutation usually manifests as a wait nobody satisfies, and without the timeout that hangs
/// for 600 s and strands a testhost.
/// </summary>
public class PackQueueLifetimeTests
{
    private static PackEntry Pack(string code) => new(
        code, $"The Sims 4 {code}", PackType.Expansion, 1000, null,
        new string('a', 64), new[] { new Uri("https://example.invalid/x.zip") }, new[] { code });

    /// <summary>
    /// Every state one pack passed through, adjacent duplicates collapsed, read from a channel that
    /// has already closed. <see cref="UpdateWatcher"/> does the same job for a queue still running;
    /// a test that only needs the history after the run must drain directly, because the updates
    /// channel is SingleReader and a watcher plus an <c>await foreach</c> would be two readers.
    /// </summary>
    private static async Task<IReadOnlyList<QueueItemState>> DrainStatesAsync(PackQueue queue, string code)
    {
        var states = new List<QueueItemState>();

        await foreach (var update in queue.Updates)
            foreach (var item in update.Items)
                if (item.Code == code && (states.Count == 0 || states[^1] != item.State))
                    states.Add(item.State);

        return states;
    }

    /// <summary>
    /// Answers every call the queue makes, on a background task, however <paramref name="answer"/>
    /// says. Awaiting one particular call instead would spend the whole timeout when a mutation
    /// kills the loop and the call never arrives; this way <c>await run</c> rethrows at once.
    /// </summary>
    private static void AnswerAll(ScriptedRunner runner, Action<ScriptedRunner.Call> answer)
    {
        _ = Task.Run(async () =>
        {
            while (true) answer(await runner.NextCallAsync());
        });
    }

    [Fact(Timeout = 15000)]
    public async Task Dispose_does_not_release_the_lock_until_the_active_item_has_stopped()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        // The default runner completes the moment its token is cancelled, so the item would have
        // released the lock before the first check below. A real ZipInstaller observes its token
        // once per entry, so a large entry keeps writing long after Cancel returns.
        var runner = new ScriptedRunner { AnswersOnCancellation = false };
        var queue = new PackQueue(runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();

        // The item is running and holds the lock.
        Assert.Null(PackLock.TryAcquire(paths, "EP01"));

        var dispose = queue.DisposeAsync();

        // Cancellation has been requested but the runner has not answered, so the item has not
        // stopped and the lock must still be held. Cancel happens before DisposeAsync's first
        // await, so this is a fact about the returned ValueTask, not a race with it.
        Assert.True(call.Token.IsCancellationRequested);
        Assert.Null(PackLock.TryAcquire(paths, "EP01"));
        Assert.False(dispose.IsCompleted);

        call.Result.SetResult(ScriptedRunner.Cancelled());
        await dispose;
        await run;

        using var acquired = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(acquired);
    }

    [Fact(Timeout = 15000)]
    public async Task A_second_dispose_racing_the_first_also_waits_for_the_active_item()
    {
        // Sequential double-dispose is harmless, but two callers at once is the ordinary shape: a
        // shell closing a window while the app-exit path runs. An early return there tells the
        // loser disposal is done while the runner is mid-entry and the pack lock is still held.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        // AnswersOnCancellation is false for the same reason as above: a runner that stops the
        // instant its token trips leaves nothing to wait for.
        var runner = new ScriptedRunner { AnswersOnCancellation = false };
        var queue = new PackQueue(runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();

        // The first call runs synchronously as far as its own await, so _disposed is already 1 when
        // the second starts: the second is deterministically the caller that lost the exchange, and
        // it is racing a first call that has not returned. No thread juggling needed for that.
        var first = queue.DisposeAsync();
        var second = queue.DisposeAsync();

        Assert.True(call.Token.IsCancellationRequested);
        Assert.Null(PackLock.TryAcquire(paths, "EP01"));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);   // the assertion an early return breaks

        call.Result.SetResult(ScriptedRunner.Cancelled());
        await second;
        await first;
        await run;

        using var acquired = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(acquired);
    }

    [Fact(Timeout = 15000)]
    public async Task Dispose_of_a_paused_queue_with_queued_work_stops_it_rather_than_draining_it()
    {
        // The pause is the case where "no more work is coming" is not enough on its own: a queue
        // paused with a Queued item parks its loop on the signal, Finished() is false, and nothing
        // else will ever release it. Complete() clears the pause for that reason and disposal
        // inherits the requirement.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        var call = await runner.NextCallAsync();
        call.Phase!.Report(PackPhase.Downloading);

        queue.Pause();
        await call.Result.Task;   // the runner's ct.Register turns the pause into a Cancelled result

        // Parked: Paused, holding the pack the pause put back in the queue.
        await watcher.WaitForAsync(u =>
            u.State == QueueState.Paused && u.Items.Count > 0 && u.Items[0].State == QueueItemState.Queued);

        await queue.DisposeAsync();
        await run;
        await watcher.Completion;

        // Disposal that merely unparked the loop would hand EP01 straight back out, and disposal
        // that left the pause standing would never return at all.
        Assert.False(runner.HasPendingCall);
        Assert.Equal(QueueState.Idle, watcher.Last!.State);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_the_run_token_mid_item_returns_normally_and_runs_no_retry_pass()
    {
        // PackQueueTests.Cancelling_the_run_token_runs_no_retry_pass cancels a loop already parked
        // in its idle branch, so it only bites when the loop reaches RequeueBlocked first. This one
        // cancels with an item in flight, pinning the loop to the top of the next iteration with
        // the token tripped, where ThrowIfCancellationRequested is all that stands between the
        // cancel and the retry pass EP02's completion just made eligible. RunAsync's catch filters
        // on the loop source rather than on ct, so `await run` must return rather than fault.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        using var holder = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(holder);

        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        // EP01 is refused its lock and blocked; EP02 is the one that reaches the runner. Finishing
        // it raises _completedCount past EP01's BlockedAtCount, which is precisely what would make
        // RequeueBlocked hand EP01 a second attempt on the next turn.
        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);

        cts.Cancel();

        // The runner answers its own cancellation; setting the result here would throw on an
        // already-completed TaskCompletionSource.
        await second.Result.Task;

        await run;   // returns, does not throw

        // A retry pass would refuse EP01 its lock again without ever calling the runner, so the
        // pending-call check alone cannot see it.
        Assert.False(runner.HasPendingCall);
        Assert.Equal(
            new[] { QueueItemState.Queued, QueueItemState.Blocked },
            await DrainStatesAsync(queue, "EP01"));
    }

    [Fact(Timeout = 15000)]
    public async Task A_second_run_throws()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });

        var run = queue.RunAsync(CancellationToken.None);

        // ThrowsAsync, not Throws: the guard sits at the top of an `async Task` method, so the
        // exception is captured into the returned task rather than thrown synchronously.
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync(CancellationToken.None));

        queue.Complete();
        await run;
    }

    [Fact(Timeout = 15000)]
    public async Task A_run_after_the_queue_has_finished_also_throws()
    {
        // The concurrent case above fails a mutated guard by wedging, since a second loop parks on
        // the signal and ThrowsAsync never gets its answer. Here the second call finds _completed
        // set and Finished() true on its first turn, so a mutated guard returns promptly and the
        // assertion fails in milliseconds. The guard has to hold after the first run has ended too.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });

        var run = queue.RunAsync(CancellationToken.None);
        queue.Complete();
        await run;

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync(CancellationToken.None));
    }

    [Fact(Timeout = 15000)]
    public async Task A_run_started_after_disposal_is_refused()
    {
        // Disposing a queue that never ran leaves _started at 0, so the once-only guard does not
        // stop a later RunAsync: items enqueued before disposal are still Queued, and the loop
        // would take their pack locks for a queue the shell has let go of, through a linked source
        // DisposeAsync has already come and gone on. ObjectDisposedException is the right answer.
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        var runner = new ScriptedRunner();
        var queue = new PackQueue(runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });

        // Answers rather than parks, so an unrefused run reaches its own end: the assertion below
        // then fails on "no exception was thrown" in milliseconds instead of waiting out a loop
        // nobody will ever answer.
        var ran = new ConcurrentQueue<string>();
        AnswerAll(runner, call =>
        {
            ran.Enqueue(call.Code);
            call.Result.SetResult(ScriptedRunner.Completed());
        });

        queue.Enqueue(Pack("EP01"), temp.Path);
        await queue.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.RunAsync(CancellationToken.None));

        // Refusing the call is only the half a caller sees; nothing may have run either.
        Assert.Empty(ran);
        using var acquired = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(acquired);
    }

    [Fact(Timeout = 15000)]
    public async Task The_last_update_carries_every_item_in_its_terminal_state()
    {
        // Shut down by cancellation rather than by Complete(). On the Complete() path the loop
        // reaches TakeNext, finds nothing queued and publishes Idle itself, so the same final
        // picture arrives either way. Cancelled mid-item, the loop throws at the top of its next
        // turn without reaching TakeNext, leaving RunAsync's finally as the only publisher.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);

        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        var second = await runner.NextCallAsync();
        Assert.Equal("EP02", second.Code);

        cts.Cancel();
        await second.Result.Task;   // the runner answers its own cancellation
        await run;

        QueueUpdate? last = null;
        await foreach (var update in queue.Updates) last = update;

        // The queue's own state and the items together: a finally that published without settling
        // the state would still carry both items correctly, and one that settled the state without
        // publishing would leave the last update saying Running.
        Assert.NotNull(last);
        Assert.Equal(QueueState.Idle, last!.State);
        Assert.Equal(QueueItemState.Completed, last.Items[0].State);
        Assert.Equal(QueueItemState.Cancelled, last.Items[1].State);
    }

    [Fact(Timeout = 15000)]
    public async Task Dispose_is_safe_twice_and_before_the_queue_has_run()
    {
        using var temp = new TempDir();
        var queue = new PackQueue(new ScriptedRunner(), new DownloadPaths(temp.Path));

        // A consumer that started reading a queue nobody ever ran must still see its enumeration
        // end: RunAsync's finally, which normally owns the channel's completion, will never run.
        var drained = Task.Run(async () =>
        {
            await foreach (var _ in queue.Updates) { }
        });

        await queue.DisposeAsync();
        await queue.DisposeAsync();

        await drained;
    }

    [Fact(Timeout = 15000)]
    public async Task Dispose_is_safe_twice_after_the_queue_has_run()
    {
        // The test above never runs the queue, so _loop is null there and the once-only guard does
        // nothing. After a run, _loop holds the source the first pass disposes and never nulls, so
        // an unguarded second pass calls Cancel() on it and throws ObjectDisposedException out of a
        // method that must be safe at any time.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(Pack("EP01"), temp.Path);
        (await runner.NextCallAsync()).Result.SetResult(ScriptedRunner.Completed());

        queue.Complete();
        await run;

        await queue.DisposeAsync();
        await queue.DisposeAsync();

        // The second pass must also leave the run's own record intact, not merely avoid throwing.
        // No phase in between: this runner reports none, so the item goes straight from Queued to
        // whatever Settle makes of the result.
        Assert.Equal(
            new[] { QueueItemState.Queued, QueueItemState.Completed },
            await DrainStatesAsync(queue, "EP01"));
    }

    [Fact(Timeout = 15000)]
    public async Task A_runner_that_throws_fails_that_item_and_the_queue_carries_on()
    {
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        AnswerAll(runner, call =>
        {
            if (call.Code == "EP01") call.Result.SetException(new InvalidOperationException("boom"));
            else call.Result.SetResult(ScriptedRunner.Completed());
        });

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);
        queue.Complete();

        await run;   // the queue survives the throw: this returns rather than faulting
        await watcher.Completion;

        var final = watcher.Last!;
        Assert.Equal(QueueItemState.Failed, final.Items[0].State);
        Assert.Contains("boom", final.Items[0].Error);
        Assert.Equal(QueueItemState.Completed, final.Items[1].State);
    }

    [Fact(Timeout = 15000)]
    public async Task A_runner_that_throws_a_foreign_cancellation_fails_that_item_too()
    {
        // An OperationCanceledException from something other than this run's token, such as an HTTP
        // read deadline, is skipped by InvokeRunnerAsync's filtered catch because
        // ct.IsCancellationRequested is false, and excluded by RunItemAsync's
        // `when (e is not OperationCanceledException)`. InvokeRunnerAsync's blanket catch is its
        // only handler; without it the exception leaves LoopAsync and faults the run.
        using var temp = new TempDir();
        var runner = new ScriptedRunner();
        await using var queue = new PackQueue(
            runner, new DownloadPaths(temp.Path), new QueueOptions { ProgressInterval = TimeSpan.Zero });
        var watcher = new UpdateWatcher(queue);
        var run = queue.RunAsync(CancellationToken.None);

        AnswerAll(runner, call =>
        {
            // The runner's own token is live; the cancellation belongs to something else entirely.
            if (call.Code == "EP01")
                call.Result.SetException(
                    new OperationCanceledException(new CancellationToken(canceled: true)));
            else
                call.Result.SetResult(ScriptedRunner.Completed());
        });

        queue.Enqueue(Pack("EP01"), temp.Path);
        queue.Enqueue(Pack("EP02"), temp.Path);
        queue.Complete();

        await run;
        await watcher.Completion;

        var final = watcher.Last!;

        // A deadline the queue did not ask for is a genuine failure, and Settle never sees it:
        // InvokeRunnerAsync marks the item itself and returns null.
        Assert.Equal(QueueItemState.Failed, final.Items[0].State);
        Assert.NotNull(final.Items[0].Error);
        Assert.Equal(QueueItemState.Completed, final.Items[1].State);
    }
}
