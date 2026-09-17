using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.App.ViewModels;

public sealed partial class UnlockerTargetViewModel(UnlockerTarget target) : ObservableObject
{
    public UnlockerTarget Target { get; } = target;
    public string ClientPath => Target.ClientPath;

    /// <summary>Flat, not a dotted binding path: MainWindowBindingTests resolves a path as a
    /// single member, so {Binding Environment.Describe()} reads as unresolved.</summary>
    public string Title => Target.Environment is { } environment
        ? $"{Target.DisplayName} ({environment.Describe()})"
        : Target.DisplayName;

    [ObservableProperty]
    private string _statusText = "Checking…";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Per row, not a banner: banners key by id, so in a batch only the last target's
    /// warning would survive.</summary>
    [ObservableProperty]
    private string? _warning;
}

/// <summary>
/// The unlocker region. RelaunchElevatedCommand lives here, not on MainViewModel: the button sits
/// in a region with its own DataContext, so a command declared there would bind by bare name
/// against this type and be silently dead. MainViewModel supplies the behaviour as a Func.
/// </summary>
public sealed partial class UnlockerViewModel : ObservableObject
{
    private readonly UnlockerService service;
    private readonly IUnlockerAssetSource assets;
    private readonly IUnlockerHost host;
    private readonly IUiDispatcher dispatcher;
    private readonly Func<Task> shutdown;
    private readonly Action exit;
    private readonly Action<Banner> banner;

    public UnlockerViewModel(
        UnlockerService service,
        IUnlockerAssetSource assets,
        IUnlockerHost host,
        IUiDispatcher dispatcher,
        Func<Task> shutdown,
        Action exit,
        Action<Banner> banner,
        UnlockerNotes? notes = null)
    {
        this.service = service;
        this.assets = assets;
        this.host = host;
        this.dispatcher = dispatcher;
        this.shutdown = shutdown;
        this.exit = exit;
        this.banner = banner;

        // Connected, not injected: the relay is created before this type exists. Dispatched
        // because a note can arrive from RunBatchAsync's pool thread, and ObservableCollection
        // raises change notifications on the calling thread.
        notes?.Connect(message => dispatcher.Post(() =>
        {
            DetectionNotes.Add(message);
            HasDetectionNotes = DetectionNotes.Count > 0;
        }));
    }

    public bool IsSupported => service.IsSupported;

    public ObservableCollection<UnlockerTargetViewModel> Targets { get; } = [];

    /// <summary>Why detection found nothing, or less than expected. Not banners: there is one per
    /// prefix and banners key by id, so the list would collapse to the last one scanned.</summary>
    public ObservableCollection<string> DetectionNotes { get; } = [];

    /// <summary>Flat, not dotted: {Binding DetectionNotes.Count} reads as unresolved and the
    /// block would silently never appear.</summary>
    [ObservableProperty]
    private bool _hasDetectionNotes;

    /// <summary>True while detection runs: a batch started then would act on rows about to be
    /// replaced.</summary>
    [ObservableProperty]
    private bool _isDetecting;

    partial void OnIsDetectingChanged(bool value) => NotifyBatchCommands();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _currentStep;

    [ObservableProperty]
    private int _completed;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RelaunchElevatedCommand))]
    private bool _requiresElevation;

    [ObservableProperty]
    private string? _batchPosition;

    /// <summary>
    /// Refused visibly, like <see cref="Start"/> refuses a re-entrant batch, and shaped the same
    /// way: the guard and the assignment cannot be one statement, or a refusal would overwrite
    /// <see cref="_detection"/> with a completed task and <see cref="DrainAsync"/> would stop
    /// waiting for the real one.
    ///
    /// Two overlapping detections are worse than wasteful: the second's Clear() runs while the
    /// first still awaits, so the first repopulates the cleared list and the second appends on top
    /// — every target listed twice, both loops mutating one ObservableCollection at once.
    /// </summary>
    public Task RefreshAsync(CancellationToken ct)
    {
        if (IsBusy || IsDetecting)
        {
            banner(new Banner("unlocker-busy",
                "The unlocker is still checking your clients, so the client list was not refreshed.",
                BannerKind.Info));
            return Task.CompletedTask;
        }

        return _detection = RefreshCoreAsync(ct);
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        IsDetecting = true;
        try
        {
            foreach (var row in Targets) row.PropertyChanged -= OnRowChanged;
            Targets.Clear();
            DetectionNotes.Clear();
            HasDetectionNotes = false;
            if (!IsSupported) return;

            // Task.Run: both callers are on the UI thread and the Wine backend's walk — two XDG
            // roots and up to eight .reg parses per prefix — never yields. The token is
            // deliberately not passed, or an already-cancelled one would fault this task.
            foreach (var target in await Task.Run(() => service.DetectAllAsync(ct)))
            {
                var row = new UnlockerTargetViewModel(target);
                row.StatusText = Describe(await Task.Run(() => service.GetStatusAsync(target, ct)));
                row.PropertyChanged += OnRowChanged;
                Targets.Add(row);
            }
        }
        finally
        {
            IsDetecting = false;
            NotifyBatchCommands();
        }
    }

    /// <summary>The operation in flight, so shutdown can wait. It is not cancellable, and exiting
    /// mid-step leaves a half-written version.dll that detection reports as "Installed".</summary>
    private Task _operation = Task.CompletedTask;

    /// <summary>The detection in flight. OnWinePrefixChanged re-detects fire-and-forget, so a
    /// scan can run unwaited and land a dispatcher.Post callback after teardown began.</summary>
    private Task _detection = Task.CompletedTask;

    /// <summary>Completes when no operation or detection is in flight. Returns <c>_operation</c>
    /// unwrapped when detection is done: a WhenAll wrapper completes one scheduled continuation
    /// later, so a caller could watch the batch finish while the drain still reports incomplete.</summary>
    public Task DrainAsync() => _detection.IsCompleted ? _operation : Task.WhenAll(_detection, _operation);

    /// <summary>Host first, shutdown second: ShutdownAsync is one-way, so a declined prompt would
    /// leave a live window with a disposed queue and no controls.</summary>
    [RelayCommand(CanExecute = nameof(RequiresElevation))]
    private async Task RelaunchElevated()
    {
        if (!host.TryRelaunchElevated())
        {
            banner(new Banner("unlocker-elevation",
                "The elevation prompt was declined, so nothing has changed.", BannerKind.Info));
            return;
        }

        await shutdown();

        // Shutdown flushes and disposes; it does not close the window, and the window greys itself
        // on IsShuttingDown. Without this the accepted prompt leaves a dead shell beside the
        // elevated instance.
        exit();
    }

    /// <summary>The rows own the selection and this type owns the commands, so CanExecute has to
    /// be re-evaluated when a row is ticked.</summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UnlockerTargetViewModel.IsSelected)) NotifyBatchCommands();
    }

    private void NotifyBatchCommands()
    {
        InstallSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunBatch() => !IsBusy && !IsDetecting && Targets.Any(t => t.IsSelected);

    /// <summary>The only route to a re-scan once the window is open; detection otherwise runs at
    /// startup and on a prefix change only. RefreshAsync already refuses re-entrantly, so this
    /// needs no CanExecute.</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Targets) row.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Targets) row.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private Task InstallSelected() => Start(
        (target, progress, ct) => service.InstallAsync(target, assets, progress, ct));

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private Task RemoveSelected() => Start(
        (target, progress, ct) => service.RemoveAsync(target, progress, ct));

    /// <summary>Leaves <c>_operation</c> alone when a batch is in flight: assigning the completed
    /// task a re-entrant call returns would make shutdown stop waiting for the real one.</summary>
    private Task Start(
        Func<UnlockerTarget, IProgress<UnlockerProgress>, CancellationToken, Task<UnlockerResult>> operation) =>
        IsBusy ? _operation : _operation = RunBatchAsync(operation);

    /// <summary>Sequential, never parallel: two targets share the configuration directory and the
    /// asset cache.</summary>
    private async Task RunBatchAsync(
        Func<UnlockerTarget, IProgress<UnlockerProgress>, CancellationToken, Task<UnlockerResult>> operation)
    {
        // Start's IsBusy ternary already refused a re-entrant call, so only IsDetecting can have
        // changed since — a detection begun after that check passed. A second call site would have
        // to preserve that guarantee itself.
        if (IsDetecting)
        {
            banner(new Banner("unlocker-busy",
                "The unlocker is still checking your clients, so nothing was started.",
                BannerKind.Info));
            return;
        }
        var rows = Targets.Where(t => t.IsSelected).ToArray();
        if (rows.Length == 0) return;

        SetBusy(true);
        RequiresElevation = false;
        var succeeded = 0;

        try
        {
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                BatchPosition = $"Target {i + 1} of {rows.Length}";
                row.Warning = null;

                // Cleared per target, not per batch: the bar would otherwise show the previous
                // target's finished count until this one's first report arrives.
                CurrentStep = null;
                Completed = 0;
                Total = 0;

                var progress = new DispatchedProgress<UnlockerProgress>(dispatcher, p =>
                {
                    CurrentStep = p.Step;
                    Completed = p.Completed;
                    Total = p.Total;
                });

                try
                {
                    var result = await Task.Run(
                        () => operation(row.Target, progress, CancellationToken.None));

                    if (result.RequiresElevation)
                    {
                        // Aborts the batch. Elevation is true of every target on the machine and
                        // the remedy is one relaunch, so continuing would collect the same failure
                        // once per target and then prompt anyway.
                        RequiresElevation = true;
                        row.StatusText = "Administrator rights required";
                        banner(new Banner("unlocker-elevation",
                            "The unlocker needs administrator rights. Nothing has been changed.",
                            BannerKind.Warning));
                        return;
                    }

                    // Hopped, like the detection call in RefreshCoreAsync: GetStatusAsync has no
                    // suspension point on either backend, so awaiting it directly runs the Wine
                    // backend's prefix reopen and registry reads on the thread that draws.
                    row.StatusText = result.Success
                        ? Describe(await Task.Run(
                            () => service.GetStatusAsync(row.Target, CancellationToken.None)))
                        : result.Error ?? "The unlocker operation failed.";
                    if (result.Success) succeeded++;

                    if (result.Warnings is { Count: > 0 } warnings)
                        row.Warning = string.Join(" ", warnings);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Scoped to its row, like a returned failure: a prefix that becomes unreadable
                    // mid-batch must not skip the targets behind it. Narrow, so a bug elsewhere
                    // still crashes loudly.
                    row.StatusText = $"The unlocker operation failed: {e.Message}";
                }
            }

            banner(new Banner("unlocker-summary",
                $"{succeeded} of {rows.Length} finished successfully.",
                succeeded == rows.Length ? BannerKind.Info : BannerKind.Warning));
        }
        finally
        {
            BatchPosition = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        foreach (var row in Targets) row.IsBusy = busy;
        NotifyBatchCommands();
    }

    private sealed class DispatchedProgress<T>(IUiDispatcher dispatcher, Action<T> apply)
        : IProgress<T>
    {
        public void Report(T value) => dispatcher.Post(() => apply(value));
    }

    private static string Describe(UnlockerStatus status) => status.State switch
    {
        UnlockerState.Installed => "Installed",
        UnlockerState.NotInstalled => "Not installed",
        _ => status.Detail ?? "Unknown",
    };
}
