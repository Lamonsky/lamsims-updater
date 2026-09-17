using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Downloading;
using LamSims.Core.Settings;

namespace LamSims.App.Tests;

public class MainViewModelLifecycleTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    /// <summary>
    /// Since this instance was constructed, which xunit does per test method. The budget below is
    /// spent across the whole test rather than reset per wait: two sequential 10s waits inside one
    /// 15s [Fact] would let xunit's timeout fire first, and the named diagnostic this exists to
    /// produce would never be seen.
    /// </summary>
    private readonly Stopwatch _test = Stopwatch.StartNew();

    /// <summary>
    /// Just inside every [Fact(Timeout = 15000)] here, so a hang always reports WHICH condition
    /// never held instead of xunit's bare timeout, while leaving a correct-but-slow run the same
    /// margin it always had.
    /// </summary>
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(13);

    // No sleeping: yielding is what lets the work being waited for run.
    // CallerArgumentExpression supplies the condition's own source text, so no call site has to
    // describe itself.
    private async Task WaitUntil(Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string? text = null)
    {
        while (!condition())
        {
            if (_test.Elapsed > TestBudget)
            {
                throw new TimeoutException(
                    $"`{text}` was still false {TestBudget.TotalSeconds:0}s into the test.");
            }

            await Task.Yield();
        }
    }

    // ---- feature detector 3 ----
    [Fact(Timeout = 15000)]
    public async Task Shutdown_cancels_before_disposing_and_never_completes_the_queue()
    {
        // Complete() is the !_completed half of RequeueBlocked's guard, so calling it makes a
        // blocked pack eligible to start; it also clears a standing pause and releases the
        // loop's signal. The loop can then take a pack lock and reach ZipInstaller, which
        // writes its journal marker before the first entry, leaving a Partial install nobody
        // asked for.
        //
        // Against a real queue this rule has no deterministic observable: both orderings end in
        // cancellation microseconds apart, and which one wins is a race. The recorded call
        // sequence is the observable, so this asserts the sequence exactly.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        await vm.ShutdownAsync();

        Assert.Equal(["CancelAll", "Dispose"], host.Queue.Calls);
        Assert.DoesNotContain("Complete", host.Queue.Calls);
    }

    [Fact(Timeout = 15000)]
    public async Task Shutdown_flushes_a_pending_settings_change_before_it_touches_the_queue()
    {
        // With the save recorded in its own list and the queue calls in another, moving the
        // flush after DisposeAsync would leave both lists unchanged, so the save is recorded
        // into the SAME list the queue records into and the interleaving is pinned.
        var order = new List<string>();
        AppSettings? flushed = null;

        var vm = TestHost.ViewModel(out var host,
            save: (settings, _) =>
            {
                order.Add("Save");
                flushed = settings;
                return Task.CompletedTask;
            },
            queue: new RecordingQueue(order));
        using var _h = host;
        vm.Connections = 5;

        await vm.ShutdownAsync();

        Assert.Equal(["Save", "CancelAll", "Dispose"], order);
        Assert.NotNull(flushed);
        Assert.Equal(5, flushed.Connections);
    }

    [Fact(Timeout = 15000)]
    public async Task Shutdown_disposes_the_queue_even_when_CancelAll_throws()
    {
        // PackQueue.CancelAll ends in cts.Cancel(), which runs registered cancellation callbacks
        // synchronously on this thread and rethrows them wrapped in an AggregateException. With
        // CancelAll and DisposeAsync guarded by one try, that throw skips the disposal: _completed
        // is never set, so the loop keeps taking Queued items and installing packs after the user
        // closed the window, with the window held open by MainWindow's re-entrancy guard while it
        // happens. Both halves are asserted: the banner alone would pass for a shutdown that
        // reported the failure and then abandoned the queue.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        host.Queue.CancelAllThrows = new AggregateException(new OperationCanceledException());

        await vm.ShutdownAsync();

        Assert.Equal(["CancelAll", "Dispose"], host.Queue.Calls);
        Assert.Contains(vm.Banners, b => b.Id == "queue" && b.Kind == BannerKind.Error);
    }

    [Fact(Timeout = 15000)]
    public async Task Starting_twice_runs_the_queue_and_builds_the_bridge_only_once()
    {
        // MainWindow.OnOpened is an async void override and is the only caller, so nothing in the
        // framework guarantees it runs once. A second StartAsync would build a second QueueBridge
        // over a single-reader channel, which the bridge's own remarks call a defect rather than a
        // degradation. "Run" appearing once is the observable for both: they are started together.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        await vm.StartAsync(CancellationToken.None);
        await vm.StartAsync(CancellationToken.None);

        // RunAsync is dispatched to the pool rather than entered on the caller's thread, so it
        // may not have been recorded by the time StartAsync returns.
        await WaitUntil(() => host.Queue.Calls.Contains("Run"));

        Assert.Equal(["Run"], host.Queue.Calls);

        await vm.ShutdownAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task The_settings_debounce_gets_a_driver_at_start_and_loses_it_at_shutdown()
    {
        // The debounce promises a write shortly after a change, and nothing was delivering it: a
        // queued save only ever reached disk through a graceful close, so a setting changed before
        // a reboot or a kill was discarded. The pair: it starts AND it stops, because a timer left
        // running past shutdown races the final flush.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        Assert.False(vm.SettingsTimerRunning);

        await vm.StartAsync(CancellationToken.None);
        Assert.True(vm.SettingsTimerRunning);

        await vm.ShutdownAsync();
        Assert.False(vm.SettingsTimerRunning);
    }

    [Fact(Timeout = 15000)]
    public async Task Shutdown_is_idempotent()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        await vm.ShutdownAsync();
        await vm.ShutdownAsync();

        Assert.Equal(["CancelAll", "Dispose"], host.Queue.Calls);
        Assert.True(vm.IsShuttingDown);
    }

    [Fact]
    public void A_queue_that_dies_disables_every_control_and_says_so()
    {
        // RunAsync can fault: EnsureCreated throws on a read-only root or a file where the
        // directory should be. Without an observer the queue is dead, Enqueue returns silently,
        // and the UI keeps accepting Add forever with no feedback.
        //
        // Every command below is put into a state where it WOULD be enabled, so each assertion
        // discriminates on IsQueueAlive and nothing else.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.GameDirectory = host.GameDirectory;
        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));

        Assert.True(vm.AddSelectedCommand.CanExecute(null));
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.CancelAllCommand.CanExecute(null));

        vm.NoteQueueClosed(new IOException("the download folder could not be created"));

        Assert.False(vm.IsQueueAlive);
        Assert.False(vm.AddSelectedCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.CancelAllCommand.CanExecute(null));
        Assert.Contains(vm.Banners, b => b.Id == "queue" && b.Kind == BannerKind.Error);
    }

    [Fact]
    public void A_queue_that_closes_cleanly_is_not_an_error()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.NoteQueueClosed(null);

        Assert.False(vm.IsQueueAlive);
        Assert.DoesNotContain(vm.Banners, b => b.Id == "queue");
    }

    [Fact(Timeout = 15000)]
    public async Task A_real_queue_that_cannot_create_its_download_folder_raises_the_banner()
    {
        // The two tests above call NoteQueueClosed directly, which proves the reaction and not
        // the wiring. This drives the only fault a real PackQueue actually produces: EnsureCreated
        // is the first statement inside RunAsync's try (PackQueue.cs:519) precisely so this throws
        // where the finally can still close the channel, and it faults the TASK, never the
        // channel, so a shell that watches only the bridge sees nothing.
        var root = Directory.CreateTempSubdirectory("lamsims-deadqueue").FullName;

        try
        {
            // A plain file where the downloads directory should be: Directory.CreateDirectory
            // throws IOException on it.
            var blocked = Path.Combine(root, "downloads");
            File.WriteAllText(blocked, "not a directory");

            var controller = new PackQueueController(
                new PackQueue(new ScriptedRunner(), new DownloadPaths(blocked)));

            var vm = TestHost.ViewModel(out var host, queue: controller);
            using var _h = host;

            await vm.StartAsync(CancellationToken.None);

            try
            {
                // Wait on the BANNER, not on IsQueueAlive: NoteQueueClosed clears the flag first
                // and raises the banner second, so waiting on the flag can return between the two
                // and read a banner list that is still empty.
                await WaitUntil(() => vm.Banners.Any(b => b.Id == "queue"));

                Assert.False(vm.IsQueueAlive);
                Assert.Contains(vm.Banners, b => b.Id == "queue" && b.Kind == BannerKind.Error);
                Assert.False(vm.AddSelectedCommand.CanExecute(null));
            }
            finally
            {
                await vm.ShutdownAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Startup_runs_the_queue_loads_the_catalog_and_scans()
    {
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(
                CatalogStatus.Loaded,
                new CatalogLoadResult(new Catalog(1, null, [Packs.Entry()]), []),
                new CatalogSource(CatalogSourceKind.Settings, "/tmp/catalog.json"), null, null)),
            seed: s => s.GameDirectory = null);
        using var _h = host;
        vm.GameDirectory = host.GameDirectory;

        await vm.StartAsync(CancellationToken.None);

        // The queue is started on the pool, so its first recorded call may trail StartAsync.
        await WaitUntil(() => host.Queue.Calls.Contains("Run"));

        Assert.Single(vm.Rows);
        Assert.True(vm.ScanCount > 0);

        host.Queue.Publish(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        await WaitUntil(() => vm.PendingCount == 1);

        await vm.ShutdownAsync();
    }

    /// <summary>
    /// One thread with a SynchronizationContext and a message pump: the shape of Avalonia's UI
    /// thread, which makes a captured continuation land back on one thread.
    /// </summary>
    private sealed class UiThread : IDisposable
    {
        private readonly BlockingCollection<Action> _work = new();

        public UiThread()
        {
            Thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new PumpContext(_work));

                foreach (var job in _work.GetConsumingEnumerable()) job();
            })
            { IsBackground = true, Name = "test-ui" };

            Thread.Start();
        }

        public Thread Thread { get; }

        public void Post(Action job) => _work.Add(job);

        public void Dispose() => _work.CompleteAdding();

        private sealed class PumpContext(BlockingCollection<Action> work) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state)
            {
                try
                {
                    work.Add(() => d(state));
                }
                catch (InvalidOperationException)
                {
                    // The pump is closed; the test is over.
                }
            }
        }
    }

    [Fact(Timeout = 15000)]
    public async Task The_queue_does_not_run_on_the_thread_that_started_it()
    {
        // MainWindow.OnOpened awaits StartAsync on Avalonia's UI thread, where a
        // SynchronizationContext is installed. Nothing in LamSims.Core or LamSims.App calls
        // ConfigureAwait, so without a deliberate hop every continuation below RunAsync (the
        // queue loop, PackWorkflow, Sha256Verifier's hash, ZipInstaller's extract) resumes on
        // that one thread and does its work there in competition with rendering.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        using var ui = new UiThread();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ui.Post(() => _ = StartOn(vm, started));

        await started.Task;
        await WaitUntil(() => host.Queue.RunThreadId != 0);

        // The pair: it ran at all, and not there. Asserting only the thread would also hold for a
        // queue that was never started, whose recorded id is zero and equals nothing.
        Assert.Contains("Run", host.Queue.Calls);
        Assert.NotEqual(ui.Thread.ManagedThreadId, host.Queue.RunThreadId);

        await vm.ShutdownAsync();
    }

    private static async Task StartOn(MainViewModel vm, TaskCompletionSource started)
    {
        try
        {
            await vm.StartAsync(CancellationToken.None);
            started.TrySetResult();
        }
        catch (Exception e)
        {
            started.TrySetException(e);
        }
    }

    // ---- M1: an unlocker operation cannot be cancelled, so shutdown has to wait for it. Exiting
    // inside step 7's File.Copy leaves the client a version.dll it cannot load, which detection
    // then reports as "Installed". ----
    [Fact(Timeout = 15000)]
    public async Task Shutdown_waits_for_an_unlocker_operation_still_in_flight()
    {
        var backend = new RecordingUnlockerBackend(
            new LamSims.Core.Unlocking.UnlockerTarget(
                "test-backend", LamSims.Core.Unlocking.ClientKind.EaApp, "/clients/ea", "EA app"));
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        var vm = TestHost.ViewModel(out var host,
            unlockerService: new LamSims.Core.Unlocking.UnlockerService([backend]));
        using var _h = host;
        await vm.Unlocker.RefreshAsync(CancellationToken.None);
        vm.Unlocker.Targets[0].IsSelected = true;
        var install = vm.Unlocker.InstallSelectedCommand.ExecuteAsync(null);
        await WaitUntil(() => backend.Calls.Count > 0);

        var shutdown = vm.ShutdownAsync();

        // The pair: shutdown has NOT finished while the operation runs, and it does finish once the
        // operation is released. The second alone would pass for a shutdown that never waited.
        Assert.False(shutdown.IsCompleted);
        hold.SetResult();
        await install;
        await shutdown;
    }
}
