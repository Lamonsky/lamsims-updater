using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Downloading;

namespace LamSims.App.Tests;

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

public class QueueBridgeTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    [Fact(Timeout = 15000)]
    public async Task An_update_reaches_the_row_it_names()
    {
        var queue = new RecordingQueue();
        var row = new PackRowViewModel(Packs.Entry(), queue);
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var bridge = new QueueBridge(queue, new ImmediateDispatcher(),
            code => code == "EP01" ? row : null, () => [row],
            _ => seen.TrySetResult(), _ => { });

        var pumping = bridge.RunAsync();
        queue.Publish(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        await seen.Task;

        Assert.Equal(QueueItemState.Downloading, row.QueueState);

        await queue.DisposeAsync();
        await pumping;
    }

    // ---- feature detector 2 ----
    [Fact(Timeout = 15000)]
    public async Task A_pack_an_update_no_longer_names_returns_to_the_state_the_scan_gave_it()
    {
        // Runs against a REAL PackQueue: hand-written updates prove nothing about the
        // interaction between the queue's publishing and its consumer. The absent-from-update
        // case is reachable for real, since Remove drops a pending item and publishes an update
        // that omits it (PackQueue.cs:439-459).
        //
        // The row must fall back to its SCANNED state, not merely stop running. A row rebuilt
        // from the catalog also reads "Not installed", so asserting that alone would pass for a
        // bridge that never resets anything. InstalledUnverified is a value only ApplyScan can
        // produce, which is what makes this discriminating.
        //
        // onUpdate fires LAST inside the post, after the absent sweep, so both observations are
        // ordered rather than racing the sweep.
        var root = Directory.CreateTempSubdirectory("lamsims-bridge").FullName;

        try
        {
            var runner = new ScriptedRunner();
            var paths = new DownloadPaths(Path.Combine(root, "downloads"));
            paths.EnsureCreated();
            var controller = new PackQueueController(
                new PackQueue(runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero }));

            var row = new PackRowViewModel(Packs.Entry(), controller);
            row.ApplyScan(new PackScanResult("EP01", PackInstallState.InstalledUnverified, [], null, null));

            // sawEp01 gates the absence watch: the update published when EP02 is enqueued also
            // lacks EP01, and without the gate it would satisfy `swept` before EP01 ever existed.
            var sawEp01 = false;
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var bridge = new QueueBridge(controller, new ImmediateDispatcher(),
                code => code == "EP01" ? row : null, () => [row],
                u =>
                {
                    if (u.Items.Any(i => i.Code == "EP01")) { sawEp01 = true; queued.TrySetResult(); }
                    else if (sawEp01) swept.TrySetResult();
                },
                _ => { });

            var run = controller.RunAsync(CancellationToken.None);
            var pumping = bridge.RunAsync();

            // EP02 first and parked in the runner: the queue is strictly sequential, so EP01 stays
            // Queued and is removable. Removing the active item is refused, which another test covers.
            controller.Enqueue(Packs.Entry("EP02", "Get Together"), root);
            await runner.Entered.Task;
            controller.Enqueue(Packs.Entry(), root);
            await queued.Task;

            Assert.Equal(QueueItemState.Queued, row.QueueState);

            Assert.True(controller.Remove("EP01"));
            await swept.Task;

            Assert.Null(row.QueueState);
            Assert.Equal("Installed (not verified by this tool)", row.StatusText);

            runner.Release();
            controller.CancelAll();
            await controller.DisposeAsync();
            await run;
            await pumping;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task The_bridge_reports_the_channel_closing()
    {
        var queue = new RecordingQueue();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var bridge = new QueueBridge(queue, new ImmediateDispatcher(), _ => null, Array.Empty<PackRowViewModel>,
            _ => { }, _ => closed.TrySetResult());

        var pumping = bridge.RunAsync();
        await queue.DisposeAsync();

        await pumping;
        await closed.Task;
    }
}
