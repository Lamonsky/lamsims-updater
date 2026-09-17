using LamSims.Core.Queueing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.Services;
using LamSims.Core.Downloading;

namespace LamSims.App.Tests;

public class PackQueueControllerTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("lamsims-controller").FullName;
    private readonly List<(PackQueueController Controller, Task Run)> _running = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Every test that starts a real queue stops it, including on the path where an assertion
    /// reds, which is why the teardown lives here rather than at the end of each test body.
    /// Both calls are idempotent, so a test that stops its own queue first costs nothing.
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var (controller, run) in _running)
        {
            controller.CancelAll();
            await controller.DisposeAsync();
            await run;
        }

        Directory.Delete(_root, recursive: true);
    }

    private (PackQueueController Controller, ScriptedRunner Runner) Start()
    {
        var runner = new ScriptedRunner();
        var paths = new DownloadPaths(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var controller = new PackQueueController(
            new PackQueue(runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero }));

        _running.Add((controller, controller.RunAsync(CancellationToken.None)));

        return (controller, runner);
    }

    /// <summary>
    /// One enumerator for the whole test. The update channel is single-reader, so a fresh
    /// <c>await foreach</c> per wait reads on from wherever the previous one stopped and a
    /// second wait can miss a transition the first already consumed.
    /// </summary>
    private sealed class Updates(IQueueController controller) : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<QueueUpdate> _updates = controller.Updates.GetAsyncEnumerator();

        public async Task<QueueUpdate> Until(Func<QueueUpdate, bool> predicate)
        {
            while (await _updates.MoveNextAsync())
            {
                if (predicate(_updates.Current)) return _updates.Current;
            }

            throw new InvalidOperationException("the queue closed before the expected update arrived");
        }

        public ValueTask DisposeAsync() => _updates.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Remove_reports_the_queues_refusal_of_the_active_item()
    {
        // TakeNext registers the active item before the runner reports any phase, so a row
        // reads Queued while it is in flight. The seam must pass core's false through rather
        // than pretending the removal happened.
        var (controller, runner) = Start();

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;

        Assert.False(controller.Remove("EP01"));

        runner.Release();
    }

    [Fact(Timeout = 15000)]
    public async Task Remove_takes_a_pending_item_out_of_the_queue()
    {
        var (controller, runner) = Start();

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;
        controller.Enqueue(Packs.Entry("EP02", "Get Together"), _root);

        Assert.True(controller.Remove("EP02"));

        runner.Release();
    }

    [Fact(Timeout = 15000)]
    public async Task Cancel_reaches_the_named_pack()
    {
        var (controller, runner) = Start();
        await using var updates = new Updates(controller);

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;
        controller.Enqueue(Packs.Entry("EP02", "Get Together"), _root);
        controller.Cancel("EP02");

        var seen = await updates.Until(u =>
            u.Items.Any(i => i.Code == "EP02" && i.State == QueueItemState.Cancelled));

        Assert.Contains(seen.Items, i => i.Code == "EP02" && i.State == QueueItemState.Cancelled);

        runner.Release();
    }

    // The view model's wiring is covered through a recording fake that implements the same
    // interface, so a controller method forwarding to the WRONG PackQueue member is invisible
    // to those tests: rewriting CancelAll() as queue.Complete() kept the whole suite green.
    // Each test below observes the real queue's own reaction rather than a recorded call name.

    [Fact(Timeout = 15000)]
    public async Task Enqueue_puts_the_pack_into_the_queues_own_updates()
    {
        var (controller, runner) = Start();
        await using var updates = new Updates(controller);

        controller.Enqueue(Packs.Entry(), _root);

        var seen = await updates.Until(u => u.Items.Any(i => i.Code == "EP01"));

        Assert.Contains(seen.Items, i => i.Code == "EP01");

        await runner.Entered.Task;
        runner.Release();
    }

    [Fact(Timeout = 15000)]
    public async Task Pause_and_Resume_move_the_queues_own_state()
    {
        // QueueState is the observable: Pause with an item in flight lands at Pausing and then
        // at Paused once the runner unwinds, and Resume puts it back to Running. A delegation
        // that reached any other PackQueue member would leave the state where it was and this
        // would run out its timeout.
        //
        // Resume needs one more step than the state alone. Complete() would ALSO have left the
        // state Running, because it clears a standing pause itself (PackQueue.cs:267-268), so
        // the resumed state does not separate them. The latch it additionally sets does: Enqueue
        // opens with `if (_completed) return;` (PackQueue.cs:193), so a pack enqueued after a
        // Complete() never appears in any update at all. The second Enqueue below is therefore
        // part of the Resume assertion, and pins that nothing here sets _completed.
        var (controller, runner) = Start();
        await using var updates = new Updates(controller);

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;
        await updates.Until(u => u.State == QueueState.Running);

        controller.Pause();
        var paused = await updates.Until(u => u.State == QueueState.Paused);

        Assert.Equal(QueueState.Paused, paused.State);

        controller.Resume();
        var resumed = await updates.Until(u => u.State == QueueState.Running);

        Assert.Equal(QueueState.Running, resumed.State);

        controller.Enqueue(Packs.Entry("EP02", "Get Together"), _root);

        var second = await updates.Until(u => u.Items.Any(i => i.Code == "EP02"));

        Assert.Contains(second.Items, i => i.Code == "EP02");

        runner.Release();
    }

    [Fact(Timeout = 15000)]
    public async Task CancelAll_ends_the_pending_pack_and_the_one_in_flight()
    {
        // Both halves matter: a pending item is marked under the lock, and the active one is
        // stopped through its run token and settles as Cancelled when the runner unwinds. No
        // Release here; the cancellation is what ends the parked runner.
        var (controller, runner) = Start();
        await using var updates = new Updates(controller);

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;
        controller.Enqueue(Packs.Entry("EP02", "Get Together"), _root);

        controller.CancelAll();

        var seen = await updates.Until(u =>
            u.Items.Count(i => i.State == QueueItemState.Cancelled) == 2);

        Assert.Equal(["EP01", "EP02"], seen.Items.Select(i => i.Code).Order());
        Assert.All(seen.Items, i => Assert.Equal(QueueItemState.Cancelled, i.State));
    }
}
