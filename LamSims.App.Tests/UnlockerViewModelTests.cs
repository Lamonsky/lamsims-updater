using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Concurrent;
using LamSims.App.ViewModels;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;
using static LamSims.App.Tests.UnlockerViewModelTests;

namespace LamSims.App.Tests;

public class UnlockerViewModelTests
{
    // Internal, not private: UnlockerDetectionNotesTests below is a separate top-level class (as
    // the brief specifies) that reuses these two rather than declaring a parallel builder, and a
    // private member is invisible outside its own class even to another type in the same file.
    internal static UnlockerTarget Target(string path = "/clients/ea", string display = "EA app",
        ClientKind kind = ClientKind.EaApp) =>
        new("test-backend", kind, path, display);

    internal static UnlockerViewModel Build(
        RecordingUnlockerBackend backend,
        IUnlockerHost? host = null,
        Func<Task>? shutdown = null,
        Action? exit = null,
        Action<Banner>? banner = null,
        UnlockerNotes? notes = null) =>
        new(new UnlockerService([backend]), new StubUnlockerAssets(), host ?? new FakeUnlockerHost(),
            new ImmediateDispatcher(), shutdown ?? (() => Task.CompletedTask), exit ?? (() => { }),
            banner ?? (_ => { }), notes);

    private static async Task<(UnlockerViewModel Vm, RecordingUnlockerBackend Backend,
                              List<Banner> Banners)> BuildBatchAsync(params UnlockerTarget[] targets)
    {
        var backend = new RecordingUnlockerBackend(targets);
        var banners = new List<Banner>();
        var vm = Build(backend, banner: banners.Add);
        await vm.RefreshAsync(CancellationToken.None);
        return (vm, backend, banners);
    }

    private static UnlockerTarget[] TwoTargets() =>
        [Target("/clients/ea", "EA app", ClientKind.EaApp),
         Target("/clients/origin", "Origin", ClientKind.Origin)];

    // Three targets cannot have three distinct ClientKinds: the enum has two members, so two of
    // these targets share a Client. The two EA rows are indistinguishable by Client, so ClientPath
    // is the only selector that generalises to any target in the set.
    private static UnlockerTarget[] ThreeTargets() =>
        [Target("/clients/ea", "EA app", ClientKind.EaApp),
         Target("/clients/origin", "Origin", ClientKind.Origin),
         Target("/clients/ea2", "EA app", ClientKind.EaApp)];

    [Fact]
    public async Task Unsupported_service_reports_no_targets_and_no_support()
    {
        var vm = new UnlockerViewModel(new UnlockerService([]), new StubUnlockerAssets(),
            new FakeUnlockerHost(), new ImmediateDispatcher(), () => Task.CompletedTask, () => { },
            _ => { });

        Assert.False(vm.IsSupported);

        await vm.RefreshAsync(CancellationToken.None);

        Assert.Empty(vm.Targets);
    }

    // The Windows shape: the EA-client backend leaves Environment null, and such a row must render
    // as the display name alone, with no trailing parenthesis.
    [Fact]
    public void Title_is_the_display_name_alone_when_there_is_no_environment()
    {
        var row = new UnlockerTargetViewModel(
            new UnlockerTarget("windows-native", ClientKind.EaApp, @"C:\EA", "EA app"));

        Assert.Equal("EA app", row.Title);
    }

    [Fact]
    public void Title_appends_the_environment_in_parentheses_when_there_is_one()
    {
        var row = new UnlockerTargetViewModel(
            new UnlockerTarget("wine-prefix", ClientKind.EaApp, "/pfx/EA", "EA app",
                Environment: new TargetEnvironment(EnvironmentSource.Lutris, "ea-app")));

        Assert.Equal("EA app (Lutris, ea-app)", row.Title);
    }

    [Fact]
    public async Task RefreshAsync_builds_one_row_per_detected_target()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target) { State = UnlockerState.Installed };
        var vm = Build(backend);

        await vm.RefreshAsync(CancellationToken.None);

        var row = Assert.Single(vm.Targets);
        Assert.Equal("EA app", row.Title);
        Assert.Equal("/clients/ea", row.ClientPath);
        Assert.Equal("Installed", row.StatusText);
    }

    // ---- pair: InstallSelectedCommand must reach the SERVICE with the SELECTED ROW'S OWN target,
    // never the collection's first entry. Asserting only the verb ("Install ran") would also pass
    // a row wired to its neighbour's data context. ----
    [Fact]
    public async Task Installing_a_selected_row_reaches_InstallAsync_with_that_rows_target()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        var backend = new RecordingUnlockerBackend(first, second);
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);

        var row = vm.Targets.Single(t => t.ClientPath == "/clients/origin");
        row.IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Contains("Install:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Install:/clients/ea", backend.Calls);
    }

    [Fact]
    public async Task Removing_a_selected_row_reaches_RemoveAsync_with_that_rows_target()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        var backend = new RecordingUnlockerBackend(first, second);
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);

        var row = vm.Targets.Single(t => t.ClientPath == "/clients/origin");
        row.IsSelected = true;
        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        Assert.Contains("Remove:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Remove:/clients/ea", backend.Calls);
    }

    [Fact]
    public async Task Progress_reports_update_step_completed_and_total()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target);
        // Asymmetric on purpose: a terminal report of 3/3 cannot tell Completed from Total, so
        // swapping the two assignments in RunBatchAsync would leave this test green.
        backend.ToReport.Add(new UnlockerProgress("Stopping the client", 1, 7));
        backend.ToReport.Add(new UnlockerProgress("Done", 5, 7));
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);

        vm.Targets[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Done", vm.CurrentStep);
        Assert.Equal(5, vm.Completed);
        Assert.Equal(7, vm.Total);
    }

    [Fact]
    public async Task IsBusy_is_true_during_the_operation_and_disables_the_batch_commands()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target);
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        var row = vm.Targets[0];
        row.IsSelected = true;

        var running = vm.InstallSelectedCommand.ExecuteAsync(null);

        // SetBusy(true) runs synchronously before the operation's Task.Run yields, so this holds
        // without waiting: the caller does not get its Task back until that point is reached.
        Assert.True(vm.IsBusy);
        Assert.True(row.IsBusy);
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        Assert.False(vm.RemoveSelectedCommand.CanExecute(null));

        hold.SetResult();
        await running;

        Assert.False(vm.IsBusy);
        Assert.False(row.IsBusy);
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
        Assert.True(vm.RemoveSelectedCommand.CanExecute(null));
    }

    // SetBusy's loop needs more than one row to be observable: with a single row in the collection,
    // an implementation that marked only the acted-on row passes. A second, untouched row is what
    // proves the busy state is shared across every row rather than scoped to the one the operation
    // is running against.
    [Fact]
    public async Task Acting_on_one_row_disables_every_other_row_too()
    {
        var acted = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var bystander = Target("/clients/origin", "Origin", ClientKind.Origin);
        var backend = new RecordingUnlockerBackend(acted, bystander);
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        var rowActedOn = vm.Targets.Single(t => t.ClientPath == "/clients/ea");
        var rowBystander = vm.Targets.Single(t => t.ClientPath == "/clients/origin");
        rowActedOn.IsSelected = true;

        var running = vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.True(rowBystander.IsBusy);
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        Assert.False(vm.RemoveSelectedCommand.CanExecute(null));

        hold.SetResult();
        await running;

        Assert.False(rowBystander.IsBusy);
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
        Assert.True(vm.RemoveSelectedCommand.CanExecute(null));
    }

    // ---- pair: a RequiresElevation result must both raise the banner AND arm the relaunch
    // command. A banner with no live command would strand the user on a dead button. ----
    [Fact]
    public async Task NeedsElevation_result_raises_a_warning_banner_and_arms_relaunch()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target) { NextResult = UnlockerResult.NeedsElevation() };
        var banners = new List<Banner>();
        var vm = Build(backend, banner: banners.Add);
        await vm.RefreshAsync(CancellationToken.None);

        Assert.False(vm.RelaunchElevatedCommand.CanExecute(null));

        vm.Targets[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.True(vm.RequiresElevation);
        Assert.True(vm.RelaunchElevatedCommand.CanExecute(null));
        Assert.Contains(banners, b => b.Id == "unlocker-elevation" && b.Kind == BannerKind.Warning);
    }

    /// <summary>Mirrors MainViewModel.ShutdownAsync's one-way guard and its queue disposal, at the
    /// grain UnlockerViewModel actually depends on: an idempotent shutdown that records into a
    /// SHARED list so the relaunch-then-shutdown order is one observable, exactly as
    /// MainViewModelLifecycleTests pins the queue's own ordering.</summary>
    private sealed class FakeShutdown(List<string> order)
    {
        public bool IsShuttingDown { get; private set; }
        public bool QueueDisposed { get; private set; }

        public Task RunAsync()
        {
            if (IsShuttingDown) return Task.CompletedTask;

            IsShuttingDown = true;
            order.Add("CancelAll");
            order.Add("Dispose");
            QueueDisposed = true;

            return Task.CompletedTask;
        }
    }

    // ---- pair: relaunch accepted -> host first, shutdown second, in that exact order. ----
    [Fact]
    public async Task RelaunchElevatedCommand_relaunches_the_host_before_shutting_down()
    {
        var order = new List<string>();
        var host = new FakeUnlockerHost(order) { RelaunchAccepted = true };
        var shutdown = new FakeShutdown(order);
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var vm = Build(backend, host: host, shutdown: shutdown.RunAsync,
                       exit: () => order.Add("Exit"));
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        await vm.RelaunchElevatedCommand.ExecuteAsync(null);

        // Exit last, and only after shutdown: shutdown does not close the window, so without it the
        // accepted prompt leaves a greyed shell beside the elevated instance for ever.
        Assert.Equal(["Relaunch", "CancelAll", "Dispose", "Exit"], order);
        Assert.True(shutdown.IsShuttingDown);
    }

    // Relaunch declined: the host was asked, but nothing about the shell was torn down. A
    // banner-only assertion would also pass a shell that shut itself down first and raised the
    // banner afterwards.
    [Fact]
    public async Task Declined_relaunch_leaves_the_shell_running_with_a_live_queue()
    {
        var order = new List<string>();
        var host = new FakeUnlockerHost(order) { RelaunchAccepted = false };
        var shutdown = new FakeShutdown(order);
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var banners = new List<Banner>();
        var vm = Build(backend, host: host, shutdown: shutdown.RunAsync, banner: banners.Add);
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        await vm.RelaunchElevatedCommand.ExecuteAsync(null);

        Assert.Equal(["Relaunch"], order);
        Assert.False(shutdown.IsShuttingDown);
        Assert.False(shutdown.QueueDisposed);
        Assert.Contains(banners, b => b.Id == "unlocker-elevation" && b.Kind == BannerKind.Info);
    }

    /// <summary>One thread with a SynchronizationContext and a message pump: the shape of
    /// Avalonia's UI thread. Mirrors MainViewModelLifecycleTests.UiThread exactly.</summary>
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
    public async Task The_operation_does_not_run_on_the_thread_that_started_it()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;

        using var ui = new UiThread();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ui.Post(() => _ = RunOn(vm, started));

        await started.Task;

        // The pair: it ran at all, and not there. Asserting only the thread would also hold for
        // an operation that never ran, whose recorded id is zero and equals nothing.
        Assert.Contains("Install:/clients/ea", backend.Calls);
        Assert.NotEqual(0, backend.OperationThreadId);
        Assert.NotEqual(ui.Thread.ManagedThreadId, backend.OperationThreadId);
    }

    private static async Task RunOn(UnlockerViewModel vm, TaskCompletionSource started)
    {
        try
        {
            await vm.InstallSelectedCommand.ExecuteAsync(null);
            started.TrySetResult();
        }
        catch (Exception e)
        {
            started.TrySetException(e);
        }
    }

    // ---- pair: the shell is not closed on a DECLINED prompt, and is on an accepted one (above). ----
    [Fact]
    public async Task Declined_relaunch_does_not_close_the_shell()
    {
        var order = new List<string>();
        var host = new FakeUnlockerHost(order) { RelaunchAccepted = false };
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var vm = Build(backend, host: host, exit: () => order.Add("Exit"));
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        await vm.RelaunchElevatedCommand.ExecuteAsync(null);

        Assert.Equal(["Relaunch"], order);
    }

    [Fact]
    public async Task DrainAsync_completes_only_once_the_operation_has()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;

        var install = vm.InstallSelectedCommand.ExecuteAsync(null);
        var drain = vm.DrainAsync();

        // The pair: it is still waiting while the operation runs AND it finishes when the operation
        // does. Asserting only the second would pass for a DrainAsync that never waited at all.
        Assert.False(drain.IsCompleted);
        hold.SetResult();
        await install;
        Assert.True(drain.IsCompleted);
    }

    [Fact]
    public async Task A_second_run_starts_from_zero_rather_than_the_last_run_s_progress()
    {
        var backend = new RecordingUnlockerBackend(Target());
        backend.ToReport.Add(new UnlockerProgress("Done", 4, 4));
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Completed);

        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        backend.ToReport.Clear();
        var second = vm.InstallSelectedCommand.ExecuteAsync(null);

        // Read DURING the second run, before it reports anything of its own: without the reset the
        // row would still be showing the first run's "Done 4/4".
        Assert.Equal(0, vm.Completed);
        Assert.Equal(0, vm.Total);
        Assert.Null(vm.CurrentStep);

        hold.SetResult();
        await second;
    }

    [Fact]
    public async Task Select_all_and_none_set_every_row()
    {
        var (vm, _, _) = await BuildBatchAsync(TwoTargets());

        vm.SelectAllCommand.Execute(null);
        Assert.All(vm.Targets, t => Assert.True(t.IsSelected));

        vm.SelectNoneCommand.Execute(null);
        Assert.All(vm.Targets, t => Assert.False(t.IsSelected));
    }

    // The rows own the selection and the region owns the commands, so the region has to observe
    // the rows. Asserting only the disabled state passes against a region that never
    // re-evaluates CanExecute, which is a permanently dead Install button.
    [Fact]
    public async Task Ticking_one_row_enables_the_batch_commands()
    {
        var (vm, _, _) = await BuildBatchAsync(TwoTargets());
        var installRaised = 0;
        var removeRaised = 0;
        vm.InstallSelectedCommand.CanExecuteChanged += (_, _) => installRaised++;
        vm.RemoveSelectedCommand.CanExecuteChanged += (_, _) => removeRaised++;

        Assert.False(vm.InstallSelectedCommand.CanExecute(null));

        vm.Targets[0].IsSelected = true;

        Assert.True(installRaised > 0);
        Assert.True(removeRaised > 0);
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
        Assert.True(vm.RemoveSelectedCommand.CanExecute(null));
    }

    // RefreshAsync replaces the rows, so it must stop listening to the old ones or a stale row
    // keeps poking the live commands. Counting subscriptions is not observable; poking the
    // discarded row is.
    [Fact]
    public async Task A_row_discarded_by_a_refresh_no_longer_drives_the_commands()
    {
        var (vm, _, _) = await BuildBatchAsync(TwoTargets());
        var stale = vm.Targets[0];
        await vm.RefreshAsync(CancellationToken.None);

        var raised = 0;
        vm.InstallSelectedCommand.CanExecuteChanged += (_, _) => raised++;
        stale.IsSelected = true;

        Assert.Equal(0, raised);
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
    }

    // Two targets share the configuration directory and the asset cache, and the engine assumes
    // one operation at a time. A handshake, not a counter: Hold keeps target 1 in flight, so
    // "target 2 has not started" is a fact rather than a race. A Task.WhenAll implementation
    // records both calls before the release and fails.
    [Fact]
    public async Task A_batch_runs_its_targets_one_at_a_time()
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());

        // Observed through OnEnter into a list this test owns, never by reading backend.Calls
        // mid-flight: Calls is appended from a pool thread, so enumerating it while a second
        // target might be running can throw "collection was modified".
        var entered = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnEnter = t => { lock (entered) entered.Add(t.ClientPath); first.TrySetResult(); };
        backend.Hold = new TaskCompletionSource();

        vm.SelectAllCommand.Execute(null);
        var batch = vm.InstallSelectedCommand.ExecuteAsync(null);
        await first.Task;

        lock (entered) Assert.Equal(["/clients/ea"], entered);

        // The deterministic discriminator: Task.WhenAll evaluates every loop body before its
        // first await, so it would already have advanced the position past target 1.
        Assert.Equal("Target 1 of 2", vm.BatchPosition);

        backend.Hold.SetResult();
        await batch;

        Assert.Equal(["Install:/clients/ea", "Install:/clients/origin"], backend.Calls);
    }

    [Fact]
    public async Task A_batch_with_nothing_selected_does_nothing()
    {
        var (vm, backend, banners) = await BuildBatchAsync(TwoTargets());

        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        // The banner list too: without the rows.Length == 0 guard the loop is empty anyway, so the
        // call count alone passes while the user is told "0 of 0 targets installed".
        Assert.Empty(backend.Calls);
        Assert.Empty(banners);
    }

    // CanRunBatch's own !IsBusy term is unpinned without this: AsyncRelayCommand's ExecutionTask
    // guard only blocks re-entry on the SAME command instance, and RemoveSelectedCommand is a
    // different instance from InstallSelectedCommand. Only this type's own IsBusy flag, shared by
    // both, catches the remove batch left enabled while the install batch is in flight.
    [Fact]
    public async Task A_running_install_batch_disables_the_remove_batch_too()
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        vm.Targets[0].IsSelected = true;

        var running = vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        Assert.False(vm.RemoveSelectedCommand.CanExecute(null));

        hold.SetResult();
        await running;

        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
        Assert.True(vm.RemoveSelectedCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mixed_selection_reaches_every_selected_target(bool remove)
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());
        vm.SelectAllCommand.Execute(null);

        await (remove ? vm.RemoveSelectedCommand : vm.InstallSelectedCommand).ExecuteAsync(null);

        var verb = remove ? "Remove" : "Install";
        Assert.Equal([$"{verb}:/clients/ea", $"{verb}:/clients/origin"], backend.Calls);
    }

    // Aborting at target 1 of 3 would leave two rows with no outcome and nothing saying why. The
    // failing target is picked by ClientPath because the two EA rows share a Client — ClientPath
    // is the only property that singles out one target regardless of position.
    [Fact]
    public async Task A_failing_target_does_not_stop_the_batch()
    {
        var (vm, backend, banners) = await BuildBatchAsync(ThreeTargets());
        backend.ResultFor = t => t.ClientPath == "/clients/origin"
            ? UnlockerResult.Fail("mirror refused")
            : UnlockerResult.Ok();

        vm.SelectAllCommand.Execute(null);
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(3, backend.Calls.Count);
        Assert.Equal("Not installed", vm.Targets[0].StatusText);
        Assert.Equal("mirror refused", vm.Targets[1].StatusText);
        Assert.Contains(banners, b => b.Text.Contains("2 of 3"));
    }

    // A returned failure and a thrown one reach the loop by different paths, and the row-scoped
    // guarantee above covers only the returned one. Without a per-row boundary the throw abandons
    // the for entirely: the target behind it is never attempted and no summary is posted, so the
    // user is left looking at a row that still reads "Not installed" with nothing saying why.
    [Fact]
    public async Task A_thrown_failure_does_not_stop_the_batch()
    {
        var (vm, backend, banners) = await BuildBatchAsync(ThreeTargets());
        backend.ThrowFor = t => t.ClientPath == "/clients/origin"
            ? new IOException("the prefix went away")
            : null;

        vm.SelectAllCommand.Execute(null);
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(3, backend.Calls.Count);
        Assert.Contains("the prefix went away", vm.Targets[1].StatusText);
        Assert.Contains(banners, b => b.Text.Contains("2 of 3"));
    }

    // The batch re-reads each row's status once its operation finishes. GetStatusAsync has no
    // suspension point on either real backend — the service delegates, and both backends return
    // Task.FromResult after doing their work — so awaiting it directly runs the Wine backend's
    // prefix reopen and registry reads on the thread that draws. OperationThreadId cannot see this
    // call, which is why the fake records status threads separately.
    [Fact]
    public async Task The_batch_status_read_does_not_run_on_the_thread_that_started_it()
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());
        var beforeBatch = backend.StatusThreadIds.Count;

        using var ui = new UiThread();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ui.Post(() => _ = RunBatchOn(vm, finished));

        await finished.Task;

        // Both halves are asserted: that the reads happened at all, and that neither landed on the
        // UI thread. Without the count, an implementation that never read a status would pass.
        var duringBatch = backend.StatusThreadIds.Skip(beforeBatch).ToArray();
        Assert.Equal(2, duringBatch.Length);
        Assert.DoesNotContain(ui.Thread.ManagedThreadId, duringBatch);
    }

    private static async Task RunBatchOn(UnlockerViewModel vm, TaskCompletionSource finished)
    {
        try
        {
            vm.SelectAllCommand.Execute(null);
            await vm.InstallSelectedCommand.ExecuteAsync(null);
            finished.TrySetResult();
        }
        catch (Exception e)
        {
            finished.TrySetException(e);
        }
    }

    // Elevation is the one exception: it is true of every target, so the batch stops and the rest
    // are never invoked. Asserting only that the prompt appeared passes against an
    // implementation that ran all three first.
    [Fact]
    public async Task Elevation_aborts_the_batch_without_touching_the_rest()
    {
        var (vm, backend, banners) = await BuildBatchAsync(ThreeTargets());
        backend.NextResult = UnlockerResult.NeedsElevation();

        vm.SelectAllCommand.Execute(null);
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["Install:/clients/ea"], backend.Calls);
        Assert.True(vm.RequiresElevation);
        Assert.True(vm.RelaunchElevatedCommand.CanExecute(null));
        Assert.Single(banners, b => b.Id == "unlocker-elevation");
        Assert.DoesNotContain(banners, b => b.Id == "unlocker-summary");
    }

    // Banners key by id so a repeated cause replaces its predecessor, which in a batch left only
    // the last target's warning visible. Asserting one warning is visible passes against exactly
    // that bug.
    [Fact]
    public async Task Every_target_keeps_its_own_warning()
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());
        backend.ResultFor = t => UnlockerResult.Ok([$"{t.ClientPath} warned"]);

        vm.SelectAllCommand.Execute(null);
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal("/clients/ea warned", vm.Targets[0].Warning);
        Assert.Equal("/clients/origin warned", vm.Targets[1].Warning);
    }

    [Fact]
    public async Task The_batch_reports_its_position_and_clears_it_afterwards()
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());
        var seen = new List<string?>();
        backend.OnEnter = _ => seen.Add(vm.BatchPosition);

        vm.SelectAllCommand.Execute(null);
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["Target 1 of 2", "Target 2 of 2"], seen);
        Assert.Null(vm.BatchPosition);
    }

    // The inner step bar is per target, so it must restart rather than carry the previous
    // target's finished count into the next one's first frame. ToReport makes target 1 finish at
    // "Done" 4/4, and OnEnter fires before the replay, so a missing reset on any one of the three
    // fields — CurrentStep, Completed, Total — is visible here. A single-field capture (Completed
    // alone) cannot catch a dropped CurrentStep or Total reset, so all three are pinned together.
    [Fact]
    public async Task Each_target_restarts_the_step_counter()
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());
        backend.ToReport.Add(new UnlockerProgress("Done", 4, 4));
        var atEntry = new List<(string? Step, int Completed, int Total)>();
        backend.OnEnter = _ => atEntry.Add((vm.CurrentStep, vm.Completed, vm.Total));

        vm.SelectAllCommand.Execute(null);
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal([(null, 0, 0), (null, 0, 0)], atEntry);
    }

    // Mirrors DrainAsync_completes_only_once_the_operation_has above, at the batch grain: without
    // Start assigning _operation, MainViewModel's shutdown (MainViewModel.cs:713) would stop
    // waiting for a batch still in flight — the same half-written version.dll failure _operation's
    // own doc comment cites. The re-entrant ExecuteAsync call, made without awaiting the first,
    // also pins Start's IsBusy ternary: reassigning _operation to a re-entrant call's own
    // already-completed task would make this drain forget the batch that is still running.
    [Fact]
    public async Task Batch_DrainAsync_waits_for_the_batch_and_a_re_entrant_call_does_not_replace_it()
    {
        var (vm, backend, _) = await BuildBatchAsync(TwoTargets());
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        vm.SelectAllCommand.Execute(null);

        var batch = vm.InstallSelectedCommand.ExecuteAsync(null);
        var drain = vm.DrainAsync();

        Assert.False(drain.IsCompleted);

        // Bypasses CanExecute the way a double-click could race it. RunBatchAsync's own IsBusy
        // guard returns immediately, so this call's task is already complete; the point is what
        // Start does with it, not what this second call itself accomplishes.
        _ = vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.False(vm.DrainAsync().IsCompleted);

        hold.SetResult();
        await batch;

        Assert.True(drain.IsCompleted);
    }

    /// <summary>
    /// Mirrors The_operation_does_not_run_on_the_thread_that_started_it exactly, at the detection
    /// grain: without a thread hop of its own in RefreshCoreAsync, the Wine backend's synchronous
    /// filesystem walk — the home directory, both XDG roots and every launcher config — would run on
    /// whatever thread called RefreshAsync, which in the shipped application is the UI thread.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Detection_does_not_run_on_the_thread_that_started_it()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var vm = Build(backend);

        using var ui = new UiThread();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ui.Post(() => _ = RunDetectOn(vm, started));

        await started.Task;

        Assert.NotEqual(0, backend.DetectThreadId);
        Assert.NotEqual(ui.Thread.ManagedThreadId, backend.DetectThreadId);
    }

    private static async Task RunDetectOn(UnlockerViewModel vm, TaskCompletionSource started)
    {
        try
        {
            await vm.RefreshAsync(CancellationToken.None);
            started.TrySetResult();
        }
        catch (Exception e)
        {
            started.TrySetException(e);
        }
    }
}

public class UnlockerDetectionNotesTests
{
    /// <summary>
    /// The notes reach the region, and HasDetectionNotes is a FLAT property because
    /// MainWindowBindingTests resolves a binding path as a single member: a
    /// {Binding DetectionNotes.Count} would be reported as unresolved and the block would never
    /// show. The pair is the point — the collection filling while the flag stays false renders
    /// nothing at all.
    /// </summary>
    [Fact]
    public async Task Detection_notes_reach_the_region()
    {
        var notes = new UnlockerNotes();
        var vm = Build(new RecordingUnlockerBackend(Target()), notes: notes);
        await vm.RefreshAsync(CancellationToken.None);

        // One of the three things the notes channel still carries. It is deliberately not a
        // per-prefix diagnostic: a 32-bit prefix or a prefix with no client registered goes to the
        // log now, and a test built on one of those would describe a route that no longer exists.
        notes.Report(
            "No Wine prefix was found. If your client is installed somewhere Steam, Lutris, Heroic, Bottles or plain Wine would not know about, set its path in the Wine prefix setting.");

        Assert.Contains("No Wine prefix was found. If your client is installed somewhere Steam, Lutris, Heroic, Bottles or plain Wine would not know about, set its path in the Wine prefix setting.",
                        vm.DetectionNotes);
        Assert.True(vm.HasDetectionNotes);
    }

    /// <summary>
    /// Cleared where Targets is cleared, so repeated detection neither accumulates nor duplicates.
    /// Without this the list grows by one full set of reasons every refresh.
    /// </summary>
    [Fact]
    public async Task A_refresh_clears_the_notes_it_replaces()
    {
        var notes = new UnlockerNotes();
        var vm = Build(new RecordingUnlockerBackend(Target()), notes: notes);

        notes.Report("stale");
        Assert.True(vm.HasDetectionNotes);

        await vm.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain("stale", vm.DetectionNotes);
        Assert.False(vm.HasDetectionNotes);
    }

    /// <summary>
    /// The exclusion, in both directions. A detection that ran during a batch would
    /// replace the rows the batch is iterating, and a batch started during detection would act on
    /// rows about to be discarded. Held open with RecordingUnlockerBackend.Hold, which is what makes
    /// "during" observable at all.
    /// </summary>
    [Fact]
    public async Task A_refresh_during_a_batch_is_refused_visibly()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var banners = new List<Banner>();
        var vm = Build(backend, banner: banners.Add);
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;

        backend.Hold = new TaskCompletionSource();
        var batch = vm.InstallSelectedCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        var rows = vm.Targets.Count;

        await vm.RefreshAsync(CancellationToken.None);

        // The rows the batch is working through are still there, and the user was told why.
        Assert.Equal(rows, vm.Targets.Count);
        Assert.Contains(banners, b => b.Id == "unlocker-busy");

        backend.Hold.SetResult();
        await batch;
    }

    [Fact]
    public async Task A_batch_during_detection_is_refused_visibly()
    {
        var banners = new List<Banner>();
        var vm = Build(new RecordingUnlockerBackend(Target()), banner: banners.Add);
        await vm.RefreshAsync(CancellationToken.None);
        vm.Targets[0].IsSelected = true;

        vm.IsDetecting = true;

        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Contains(banners, b => b.Id == "unlocker-busy");
    }

    /// <summary>
    /// Detection is not mutually exclusive with itself without this: the second call's own
    /// synchronous Clear() would run while the first is still awaiting DetectAllAsync, so the first
    /// resumes and repopulates the just-cleared list, and the second then appends its own scan on
    /// top without ever re-clearing. Both loops then add to the same non-thread-safe
    /// ObservableCollection without coordination, so what actually lands there is unpredictable: a
    /// captured run showed one genuine row plus a null slot torn from a concurrent List resize
    /// rather than two clean duplicates, which is why the assertion below checks a count rather
    /// than specific values. HoldDetect is what makes "during" observable: without it neither call
    /// would still be in flight when the other starts.
    /// </summary>
    [Fact]
    public async Task A_refresh_during_another_refresh_is_refused_and_does_not_duplicate_targets()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var banners = new List<Banner>();
        var vm = Build(backend, banner: banners.Add);

        backend.HoldDetect = new TaskCompletionSource();
        var first = vm.RefreshAsync(CancellationToken.None);
        Assert.True(vm.IsDetecting);

        var second = vm.RefreshAsync(CancellationToken.None);

        backend.HoldDetect.SetResult();
        await Task.WhenAll(first, second);

        // The discriminating assertion first: a count, not merely "some rows exist". Without the
        // guard this is 2 for a single detected target.
        Assert.Single(vm.Targets);

        Assert.Contains(banners, b => b.Id == "unlocker-busy");
    }

    /// <summary>
    /// Mirrors DrainAsync_completes_only_once_the_operation_has, at the detection grain: before
    /// this task, detection only ever ran from StartAsync, which is awaited, so nothing could still
    /// be running at shutdown. OnWinePrefixChanged's fire-and-forget re-detection is the first path
    /// where it can, and DrainAsync has to wait for it too or a dispatched note/row update can land
    /// after teardown has begun.
    /// </summary>
    [Fact]
    public async Task DrainAsync_waits_for_an_in_flight_detection_too()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var vm = Build(backend);

        backend.HoldDetect = new TaskCompletionSource();
        var refresh = vm.RefreshAsync(CancellationToken.None);
        var drain = vm.DrainAsync();

        Assert.False(drain.IsCompleted);

        backend.HoldDetect.SetResult();
        await refresh;

        Assert.True(drain.IsCompleted);
    }
}

/// <summary>
/// Every test above builds a bare <see cref="UnlockerViewModel"/>, so none of them notice if
/// <see cref="MainViewModel"/> wired <c>shutdown</c> or <c>banner</c> to a no-op instead of to
/// <c>ShutdownAsync</c>/<c>Raise</c>: the delegate types still match, so it still compiles and
/// every other test still passes. These two drive a REAL <see cref="MainViewModel"/> built
/// through <see cref="TestHost"/>, with a recording backend substituted for
/// <see cref="AppServices.Unlocker"/>, so the wiring itself is what is under test.
/// </summary>
public class MainViewModelUnlockerWiringTests
{
    private static UnlockerTarget Target() =>
        new("test-backend", ClientKind.EaApp, "/clients/ea", "EA app");

    [Fact(Timeout = 15000)]
    public async Task RelaunchElevatedCommand_reaches_the_real_ShutdownAsync()
    {
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var host = new FakeUnlockerHost { RelaunchAccepted = true };

        var vm = TestHost.ViewModel(out var hostFixture,
            unlockerService: new UnlockerService([backend]),
            unlockerHost: host,
            unlockerAssets: new StubUnlockerAssets());
        using var _h = hostFixture;

        await vm.Unlocker.RefreshAsync(CancellationToken.None);
        vm.Unlocker.Targets[0].IsSelected = true;
        await vm.Unlocker.InstallSelectedCommand.ExecuteAsync(null);
        Assert.True(vm.Unlocker.RelaunchElevatedCommand.CanExecute(null));

        await vm.Unlocker.RelaunchElevatedCommand.ExecuteAsync(null);

        // Both halves, per the review: IsShuttingDown alone could be set by something unrelated,
        // and the queue's recorded calls alone would not prove WHICH shutdown ran. Together they
        // prove the injected delegate reached MainViewModel's own ShutdownAsync.
        Assert.True(vm.IsShuttingDown);
        Assert.Equal(["CancelAll", "Dispose"], hostFixture.Queue.Calls);
        Assert.Contains("Relaunch", host.Calls);
    }

    [Fact(Timeout = 15000)]
    public async Task NeedsElevation_result_reaches_the_real_Raise()
    {
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };

        var vm = TestHost.ViewModel(out var hostFixture,
            unlockerService: new UnlockerService([backend]),
            unlockerAssets: new StubUnlockerAssets());
        using var _h = hostFixture;

        await vm.Unlocker.RefreshAsync(CancellationToken.None);

        vm.Unlocker.Targets[0].IsSelected = true;
        await vm.Unlocker.InstallSelectedCommand.ExecuteAsync(null);

        // Only a banner that reached MainViewModel.Banners through the real Raise proves the
        // wiring; Raise dismissing by id is also what keeps a second elevation report from
        // stacking a duplicate.
        Assert.Contains(vm.Banners, b => b.Id == "unlocker-elevation" && b.Kind == BannerKind.Warning);
    }

    /// <summary>
    /// Nothing in the shipped application called RefreshAsync at all, so on a real Windows machine
    /// with a supported client installed, IsSupported was true and Targets stayed empty forever:
    /// a DLC Unlocker section with rows that never appear. Calls StartAsync and never RefreshAsync
    /// directly, because what is under test is that startup itself does the refresh; a test that
    /// called RefreshAsync itself could not have caught this.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task StartAsync_refreshes_the_unlocker_so_a_supported_client_has_a_row_at_startup()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target);

        var vm = TestHost.ViewModel(out var hostFixture, unlockerService: new UnlockerService([backend]));
        using var _h = hostFixture;

        await vm.StartAsync(CancellationToken.None);

        // The pair: a row count alone would also pass a refresh that invented an empty row from
        // nowhere. Only the ClientPath proves the row came from THIS target's detection.
        var row = Assert.Single(vm.Unlocker.Targets);
        Assert.Equal(target.ClientPath, row.ClientPath);
    }
}
