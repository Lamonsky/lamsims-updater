using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Logging;
using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using LamSims.Core.Settings;

namespace LamSims.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<CancellationToken, Task<CatalogResolution>> _resolve;
    private readonly Func<CatalogSource, CancellationToken, Task<CatalogResolution>> _load;
    private readonly Func<AppSettings, CancellationToken, Task> _save;
    private readonly ProgressTicker _ticker;

    public MainViewModel(
        AppServices services,
        Func<CancellationToken, Task<CatalogResolution>>? resolve = null,
        Func<CatalogSource, CancellationToken, Task<CatalogResolution>>? load = null,
        Func<AppSettings, CancellationToken, Task>? save = null)
    {
        _services = services;
        _resolve = resolve ?? (ct => services.Catalog.ResolveAsync(
            services.CommandLineCatalog, services.Current.CatalogSource, ct));
        _load = load ?? services.Catalog.LoadAsync;
        _save = save ?? services.Settings.SaveAsync;

        // Not in Composition: callers swap in their own Dispatcher after Build returns, and one
        // built there would have captured the discarded one.
        Log = new LogViewModel(services.Log, services.Dispatcher, services.Clipboard);
        _ticker = new ProgressTicker(services.Clock, services.Log);

        // Not through the setters: those queue a save and move the engine. A value the user
        // already chose is not a change.
        _gameDirectory = services.Current.GameDirectory;
        _downloadDirectory = services.Current.DownloadDirectory;
        _connections = services.Current.Connections;
        _catalogInput = services.Current.CatalogSource ?? "";
        _winePrefix = services.Current.WinePrefix;

        if (services.SettingsError is { } settingsError)
        {
            Raise(new Banner("settings-load",
                $"Your settings could not be fully applied and defaults are in use: {settingsError}",
                BannerKind.Warning));
        }

        Unlocker = new UnlockerViewModel(
            services.Unlocker,
            services.UnlockerAssets,
            services.UnlockerHost,
            services.Dispatcher,
            shutdown: ShutdownAsync,
            exit: () => RequestClose?.Invoke(),
            banner: Raise,
            services.UnlockerNotes);
    }

    public UnlockerViewModel Unlocker { get; }

    public LogViewModel Log { get; }

    /// <summary>Set by the window. The elevated relaunch is the one path that must close it from
    /// here, since ShutdownAsync alone leaves the shell up.</summary>
    public Action? RequestClose { get; set; }

    // These wrappers keep the three delegate fields read: an assigned-never-read private field is
    // CS0414, and this repository builds warnings as errors. An uncalled private method is not.
    private Task<CatalogResolution> ResolveCatalogAsync(CancellationToken ct) => _resolve(ct);

    private Task<CatalogResolution> LoadCatalogFromAsync(CatalogSource source, CancellationToken ct) =>
        _load(source, ct);

    private Task PersistSettingsAsync(CancellationToken ct) => _save(_services.Current, ct);

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private DateTimeOffset? _saveDueAt;

    internal int SettingsWriteCount { get; private set; }

    partial void OnConnectionsChanged(int value)
    {
        _services.Current.Connections = value;

        // Read per download, and the shared pool is sized for MaxConnections, so this takes effect
        // from the next pack with nothing to rebuild.
        _services.DownloadOptions.Connections = Math.Clamp(value, 1, DownloadOptions.MaxConnections);

        QueueSave();
    }

    partial void OnDownloadDirectoryChanged(string? value)
    {
        if (_revertingDownloadDirectory) return;

        if (!MoveDownloadsTo(value))
        {
            // The engine stayed put, so the setting must too: persisting a root that cannot be
            // created loses the working one on the next launch.
            _revertingDownloadDirectory = true;
            try { DownloadDirectory = _services.Current.DownloadDirectory; }
            finally { _revertingDownloadDirectory = false; }
            return;
        }

        _services.Current.DownloadDirectory = value;
        QueueSave();
    }

    private bool _revertingDownloadDirectory;

    /// <summary>
    /// Points the engine at a new download directory. Every consumer asks the one
    /// <see cref="DownloadPaths"/> per call, so this moves all of them at once. Gated on an empty
    /// queue: a move under a live download orphans its <c>.part</c> file and its lock.
    /// </summary>
    /// <returns>False when the directory was refused and the engine stayed where it was.</returns>
    private bool MoveDownloadsTo(string? directory)
    {
        try
        {
            _services.DownloadPaths.Retarget(directory);
            Dismiss("downloads");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            // Retarget assigns nothing until the directory exists, so the engine is still on the
            // root it was already using, which is what the banner names.
            Raise(new Banner("downloads",
                $"Downloads are still being kept in '{_services.DownloadPaths.Root}': {e.Message}",
                BannerKind.Warning));
            return false;
        }
    }

    private bool _sweptOrphans;

    internal int OrphansSwept { get; private set; }

    /// <summary>
    /// A .part file for a pack the catalog no longer lists is unreachable from inside the app.
    /// Once per session, and only off a catalog that loaded — the known codes are what protect
    /// every pack still listed. Archives are never touched.
    /// </summary>
    private void SweepOrphansOnce(IReadOnlySet<string> knownCodes)
    {
        if (_sweptOrphans) return;

        _sweptOrphans = true;

        try
        {
            OrphansSwept = _services.Orphans.CleanOrphans(knownCodes).Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort. Failing to reclaim space is not worth interrupting a catalog load.
        }
    }

    private void QueueSave() => _saveDueAt = _services.Clock.UtcNow + SaveDebounce;

    private Timer? _saveTimer;

    /// <summary>Whether the debounce has a driver. Asserted rather than waited on: the tick is
    /// real time and this suite does not sleep.</summary>
    internal bool SettingsTimerRunning => _saveTimer is not null;

    /// <summary>Writes a pending change only once the debounce has elapsed. Driven by a timer.</summary>
    public Task FlushDueSettingsAsync(CancellationToken ct) =>
        _saveDueAt is { } due && _services.Clock.UtcNow >= due
            ? FlushSettingsNowAsync(ct)
            : Task.CompletedTask;

    /// <summary>Writes a pending change immediately. Used at shutdown and after a picker.</summary>
    public async Task FlushSettingsNowAsync(CancellationToken ct)
    {
        if (_saveDueAt is null) return;

        _saveDueAt = null;

        try
        {
            await PersistSettingsAsync(ct);
            SettingsWriteCount++;
            Dismiss("settings");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Raise(new Banner("settings", $"Your settings could not be saved: {e.Message}", BannerKind.Warning));
        }
    }

    [RelayCommand]
    private async Task BrowseGameAsync(CancellationToken ct)
    {
        var picked = await _services.Pickers.PickFolderAsync("Choose the Sims 4 folder", GameDirectory);
        if (picked is null) return;

        GameDirectory = picked;
        _services.Current.GameDirectory = picked;
        QueueSave();
        await FlushSettingsNowAsync(ct);
        Scan();
    }

    /// <summary>Refused while anything is queued: a move leaves the <c>.part</c> file and the
    /// lock in the old directory, still being written and never looked at again.</summary>
    public bool CanMoveDownloads => PendingCount == 0;

    [RelayCommand(CanExecute = nameof(CanMoveDownloads))]
    private async Task BrowseDownloadsAsync(CancellationToken ct)
    {
        var picked = await _services.Pickers.PickFolderAsync("Choose where downloads are kept", DownloadDirectory);
        if (picked is null) return;

        DownloadDirectory = picked;   // its setter moves the engine and queues the save
        await FlushSettingsNowAsync(ct);
    }

    [RelayCommand]
    private async Task BrowseWinePrefixAsync(CancellationToken ct)
    {
        var picked = await _services.Pickers.PickFolderAsync("Choose a Wine prefix", WinePrefix);
        if (picked is null) return;

        WinePrefix = picked;   // its setter saves and re-runs detection
        await FlushSettingsNowAsync(ct);
    }

    public ObservableCollection<Banner> Banners { get; } = new();

    [ObservableProperty]
    private string _emptyStateMessage = "";

    [ObservableProperty]
    private string _catalogDescription = "";

    private CatalogSource? _cachedCopy;

    public async Task LoadCatalogAsync(CancellationToken ct)
    {
        CatalogResolution resolution;

        try
        {
            resolution = await ResolveCatalogAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The application is closing. Not a catalog error, and not a banner.
            return;
        }

        Apply(resolution);
    }

    private void Apply(CatalogResolution resolution)
    {
        _cachedCopy = resolution.CachedCopy;
        UseCachedCatalogCommand.NotifyCanExecuteChanged();
        Dismiss("catalog");
        Dismiss("catalog-rejected");

        switch (resolution.Status)
        {
            case CatalogStatus.Loaded when resolution.Load is { } load:
                BuildRows(load.Catalog.Packs);
                CatalogDescription = resolution.Source?.Location ?? "";
                EmptyStateMessage = load.Catalog.Packs.Count == 0 ? "This catalog lists no packs." : "";

                if (load.Rejected.Count > 0)
                {
                    Raise(new Banner("catalog-rejected",
                        $"{load.Rejected.Count} catalog entr{(load.Rejected.Count == 1 ? "y was" : "ies were")} "
                        + $"skipped. First: {load.Rejected[0].Description} — {load.Rejected[0].Reason}",
                        BannerKind.Warning));
                }

                SweepOrphansOnce(load.Catalog.KnownCodes);
                break;

            case CatalogStatus.Empty:
                BuildRows([]);
                CatalogDescription = "";
                EmptyStateMessage = "No catalog selected.";
                break;

            default:
                BuildRows([]);
                CatalogDescription = "";
                EmptyStateMessage = "The catalog could not be loaded.";
                Raise(new Banner("catalog", resolution.Error ?? "The catalog could not be loaded.", BannerKind.Error));
                break;
        }

        // A fresh row knows nothing about what is on disk, and the catalog-change paths reach the
        // rows only through here: without this an installed pack reads "Not installed" and Add
        // re-downloads it.
        if (!string.IsNullOrWhiteSpace(GameDirectory)) Scan();
    }

    private void Raise(Banner banner)
    {
        Dismiss(banner.Id);
        Banners.Add(banner);

        // A banner is the one thing a user is guaranteed to have seen; the log keeps it after the
        // banner itself is dismissed.
        _services.Log.Write(banner.Kind switch
        {
            BannerKind.Error => LogLine.Error(banner.Text),
            BannerKind.Warning => LogLine.Warning(banner.Text),
            _ => LogLine.Info(banner.Text),
        });
    }

    private void Dismiss(string id)
    {
        for (var i = Banners.Count - 1; i >= 0; i--)
        {
            if (Banners[i].Id == id) Banners.RemoveAt(i);
        }
    }

    [RelayCommand]
    private void DismissBanner(Banner banner) => Banners.Remove(banner);

    /// <summary>Refused while anything is queued: a rebuilt row list orphans an in-flight pack,
    /// which keeps its lock and bandwidth with no row to cancel it from.</summary>
    public bool CanChangeCatalog => PendingCount == 0;

    [RelayCommand(CanExecute = nameof(CanChangeCatalog))]
    private async Task ChangeCatalogAsync(CancellationToken ct)
    {
        var picked = await _services.Pickers.PickFileAsync("Choose a catalog file", _services.Paths.Root);
        if (picked is null) return;

        CatalogInput = picked;
        _services.Current.CatalogSource = picked;
        await SaveSettingsAsync(ct);
        Apply(await LoadCatalogFromAsync(new CatalogSource(CatalogSourceKind.Settings, picked), ct));
    }

    /// <summary>What the user typed into the catalog box. Distinct from
    /// <see cref="CatalogDescription"/>, which reports the source in use; the two differ whenever
    /// the box holds an unapplied edit.</summary>
    [ObservableProperty]
    private string _catalogInput = "";

    [RelayCommand(CanExecute = nameof(CanChangeCatalog))]
    private async Task ApplyCatalogSourceAsync(CancellationToken ct)
    {
        // A URL pasted from a browser or a chat client routinely carries surrounding whitespace,
        // and neither Uri.TryCreate nor the local reader forgives it.
        var typed = CatalogInput.Trim();
        CatalogInput = typed;

        _services.Current.CatalogSource = typed.Length == 0 ? null : typed;
        await SaveSettingsAsync(ct);

        if (typed.Length == 0)
        {
            // Emptying the box is how the user gets back to "no catalog chosen"; the cached copy
            // is unaffected by it and stays on offer.
            Apply(new CatalogResolution(CatalogStatus.Empty, null, null, null, _cachedCopy));
            return;
        }

        Apply(await LoadCatalogFromAsync(new CatalogSource(CatalogSourceKind.Settings, typed), ct));
    }

    private bool CanUseCachedCatalog => _cachedCopy is not null && PendingCount == 0;

    [RelayCommand(CanExecute = nameof(CanUseCachedCatalog))]
    public async Task UseCachedCatalogAsync(CancellationToken ct)
    {
        if (_cachedCopy is not { } source) return;

        Apply(await LoadCatalogFromAsync(source, ct));
    }

    private Task SaveSettingsAsync(CancellationToken ct)
    {
        QueueSave();
        return FlushSettingsNowAsync(ct);
    }

    public ObservableCollection<PackRowViewModel> Rows { get; } = new();

    public IReadOnlyList<PackRowViewModel> FilteredRows =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Rows
            : Rows.Where(r =>
                r.Code.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredRows))]
    private string _searchText = "";

    [ObservableProperty]
    private string? _gameDirectory;

    [ObservableProperty]
    private bool _gameDirectoryReadable = true;

    [ObservableProperty]
    private bool _isQueueAlive = true;

    [ObservableProperty]
    private string? _downloadDirectory;

    [ObservableProperty]
    private int _connections = 8;

    [ObservableProperty]
    private string? _winePrefix;

    /// <summary>Applied live: the scanner reads the setting per scan, so re-running detection is
    /// all that is needed.</summary>
    partial void OnWinePrefixChanged(string? value)
    {
        _services.Current.WinePrefix = value;
        QueueSave();
        _ = DetectUnlockerAsync(CancellationToken.None);
    }

    public QueueState QueueState { get; private set; } = QueueState.Idle;

    public int PendingCount { get; private set; }

    public int FailedCount { get; private set; }

    public string StatusText => QueueState switch
    {
        QueueState.Running => $"Running — {PendingCount} left{FailedText}",
        QueueState.Pausing => $"Pausing — {PendingCount} left{FailedText}",
        QueueState.Paused => $"Paused — {PendingCount} left{FailedText}",
        _ => PendingCount == 0 && FailedCount == 0 ? "Idle" : $"Idle — {PendingCount} left{FailedText}",
    };

    private string FailedText => FailedCount == 0 ? "" : $", {FailedCount} failed";

    public void BuildRows(IEnumerable<PackEntry> entries)
    {
        Rows.Clear();

        // Keyed by code, and the rows it described are gone: left standing, a rebuilt row would
        // adopt an echoed Completed with no rescan to retire the overlay.
        _rescanned.Clear();

        foreach (var entry in entries) Rows.Add(new PackRowViewModel(entry, _services.Queue));

        OnPropertyChanged(nameof(FilteredRows));
    }

    public PackRowViewModel? RowFor(string code) =>
        Rows.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal));

    public void ApplyUpdate(QueueUpdate update)
    {
        // IsFinal also excludes a twice-blocked pack, which the queue will not retry by itself.
        // Counting one kept CanChangeCatalog false for the rest of the session.
        PendingCount = update.Items.Count(i => !i.IsFinal && i.State
            is not (QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled));
        FailedCount = update.Items.Count(i => i.State == QueueItemState.Failed);

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(FailedCount));

        ChangeCatalogCommand.NotifyCanExecuteChanged();
        ApplyCatalogSourceCommand.NotifyCanExecuteChanged();
        UseCachedCatalogCommand.NotifyCanExecuteChanged();
        BrowseDownloadsCommand.NotifyCanExecuteChanged();

        ApplyQueueState(update.State);

        foreach (var item in update.Items) _ticker.Observe(item);

        // Every update carries every item, so a completion echoes all session and the set stops
        // the rescan repeating. It must also be released: otherwise a re-enqueued pack's second
        // completion fires no rescan and the row stays pinned at "Installed".
        foreach (var item in update.Items)
        {
            if (item.State != QueueItemState.Completed) _rescanned.Remove(item.Code);
        }

        if (update.Items.Any(i => i.State == QueueItemState.Completed && _rescanned.Add(i.Code))) Scan();
    }

    private readonly HashSet<string> _rescanned = new(StringComparer.Ordinal);

    /// <summary>Test-visible: proves the rescan fires once per completion, not once per update.</summary>
    internal int ScanCount { get; private set; }

    [RelayCommand]
    private void Scan()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory)) return;

        ScanCount++;

        // Both calls are pure and total by core's contract (neither writes, and neither throws
        // on a marker's content), so this command has no error path of its own.
        var markers = _services.InstallState.LoadAll(GameDirectory);
        var result = InstallScanner.Scan(GameDirectory, Rows.Select(r => r.Entry), markers);

        // InstalledUnverified is an installed state, not a warning; see InstallScanner.
        var installedCount = result.Packs.Count(p =>
            p.State is PackInstallState.Installed or PackInstallState.InstalledUnverified);
        var partialCount = result.Packs.Count(p => p.State == PackInstallState.Partial);

        _services.Log.Write(LogLine.Info($"Scan found {installedCount} installed, {partialCount} partial"));

        GameDirectoryReadable = result.GameDirectoryReadable;

        if (result.GameDirectoryReadable)
        {
            Dismiss("game-directory");
            CheckGameFolderLayout();
        }
        else
        {
            Raise(new Banner("game-directory",
                $"The game folder '{GameDirectory}' could not be read, so no pack can be shown as installed.",
                BannerKind.Error));

            // A layout warning about a directory that cannot be read at all says less than the
            // read failure beside it, and two banners for one directory read as two problems.
            Dismiss("game-root");
        }

        // Ordinal: the scanner builds every result from the catalog's own pack.Code, so these are
        // the rows' strings. Its case-insensitive store never reaches the results.
        foreach (var scan in result.Packs) RowFor(scan.Code)?.ApplyScan(scan);
    }

    /// <summary>
    /// Warns when the chosen folder is not a Sims 4 installation, which the scan cannot say on its
    /// own: the saves folder of the same name is perfectly readable and simply holds no pack, so
    /// every row reads NotInstalled and an install into it looks like it worked.
    ///
    /// A warning, never a refusal. The layouts are EA's and Valve's to change, and the check has to
    /// be wrong about a real installation without costing the user the install.
    /// </summary>
    private void CheckGameFolderLayout()
    {
        var bin = Path.Combine("Game", "Bin");

        var message = GameRootCheck.Inspect(GameDirectory) switch
        {
            GameRootVerdict.SavesFolder =>
                $"'{GameDirectory}' is the Sims 4 saves folder, not the installation. Packs "
                + $"installed there stay invisible to the game: choose the folder holding {bin}.",
            GameRootVerdict.Unrecognised =>
                $"'{GameDirectory}' holds no {bin}, so it does not look like a Sims 4 installation. "
                + "Packs installed there stay invisible to the game.",
            _ => null,
        };

        if (message is null) Dismiss("game-root");
        else Raise(new Banner("game-root", message, BannerKind.Warning));
    }

    public void ApplyQueueState(QueueState state)
    {
        QueueState = state;

        OnPropertyChanged(nameof(QueueState));
        OnPropertyChanged(nameof(StatusText));
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in FilteredRows.Where(r => r.IsCheckable)) row.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Rows) row.IsChecked = false;
    }

    private bool CanAdd => IsQueueAlive && !string.IsNullOrWhiteSpace(GameDirectory) && GameDirectoryReadable;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSelected()
    {
        foreach (var row in Rows.Where(r => r.IsChecked && r.IsCheckable).ToList())
        {
            _services.Queue.Enqueue(row.Entry, GameDirectory!);
            row.IsChecked = false;
        }
    }

    private bool CanPause => IsQueueAlive && QueueState == QueueState.Running;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _services.Queue.Pause();

    private bool CanResume => IsQueueAlive && QueueState is QueueState.Paused or QueueState.Pausing;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _services.Queue.Resume();

    private bool CanCancelAll => IsQueueAlive && PendingCount > 0;

    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll() => _services.Queue.CancelAll();

    partial void OnGameDirectoryChanged(string? value) => AddSelectedCommand.NotifyCanExecuteChanged();

    partial void OnGameDirectoryReadableChanged(bool value) => AddSelectedCommand.NotifyCanExecuteChanged();

    partial void OnIsQueueAliveChanged(bool value)
    {
        AddSelectedCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
    }

    private QueueBridge? _bridge;
    private Task? _queueRun;
    private Task? _bridgeRun;

    [ObservableProperty]
    private bool _isShuttingDown;

    private bool _started;

    public async Task StartAsync(CancellationToken ct)
    {
        // The only caller is an `async void` override, which nothing guarantees runs once. A
        // second QueueBridge over a single-reader channel would split the updates in two.
        if (_started) return;

        _started = true;

        _bridge = new QueueBridge(
            _services.Queue, _services.Dispatcher, RowFor, () => Rows, ApplyUpdate, NoteQueueClosed);

        _queueRun = ObserveQueueAsync(ct);
        _bridgeRun = _bridge.RunAsync();

        // Without this a queued save only reached disk on a graceful close, so a slider moved
        // before a kill was discarded. The flush is a no-op until the debounce elapses.
        _saveTimer = new Timer(
            _ => _services.Dispatcher.Post(() => _ = FlushDueSettingsAsync(CancellationToken.None)),
            null, SaveDebounce, SaveDebounce);

        SweepAssetTemps();

        await DetectUnlockerAsync(ct);

        await LoadCatalogAsync(ct);

        if (!string.IsNullOrWhiteSpace(GameDirectory)) Scan();
    }

    internal int AssetTempsSwept { get; private set; }

    /// <summary>Before detection, because this is only safe while no fetch can have started: an
    /// asset temp carries no pack code, so nothing tells a live one from an abandoned one.</summary>
    private void SweepAssetTemps()
    {
        try
        {
            AssetTempsSwept = _services.Orphans.CleanAssetTemps().Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, like the orphan sweep: reclaiming space is not worth failing a start.
        }
    }

    /// <summary>
    /// Runs before the catalog load. Windows detection is a handful of registry opens; the Wine
    /// backend walks the prefix setting, <c>$WINEPREFIX</c> and five launcher sources, each
    /// doubled for Flatpak, parsing up to eight <c>.reg</c> files per prefix. Without it,
    /// IsSupported can be true with Targets empty — a DLC Unlocker section with no rows.
    ///
    /// UnlockerViewModel.RefreshCoreAsync hops onto Task.Run for the walk; this method is awaited
    /// from OnOpened, so without that hop it would run on the thread that draws.
    ///
    /// Guarded narrowly: IOException and UnauthorizedAccessException are the only exceptions
    /// either backend surfaces, so a bug elsewhere still crashes loudly.
    /// </summary>
    private async Task DetectUnlockerAsync(CancellationToken ct)
    {
        try
        {
            await Unlocker.RefreshAsync(ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Raise(new Banner("unlocker-detect",
                $"The DLC unlocker could not check your installed clients: {e.Message}", BannerKind.Warning));
        }
    }

    /// <summary>
    /// The queue's fault arrives here, not through the bridge: RunAsync's finally always completes
    /// the channel without an argument, so the bridge's fault is always null against a real queue.
    /// Unobserved, the exception would surface as an unhandled crash on window close.
    /// </summary>
    private async Task ObserveQueueAsync(CancellationToken ct)
    {
        try
        {
            // Task.Run because nothing here calls ConfigureAwait: without the hop the hash and the
            // extract run on the thread that draws. The token is deliberately not passed — an
            // already-cancelled one would fault this task rather than let RunAsync return.
            await Task.Run(() => _services.Queue.RunAsync(ct));
        }
        catch (Exception e)
        {
            // Through the dispatcher: RunAsync's continuation is not on the UI thread.
            _services.Dispatcher.Post(() => NoteQueueClosed(e));
        }
    }

    /// <summary>The queue has ended. Called by the bridge (always a null fault, see
    /// <see cref="ObserveQueueAsync"/>) and by ObserveQueueAsync, the only place a real fault is
    /// seen. Both disable the controls; only a fault banners.</summary>
    public void NoteQueueClosed(Exception? fault)
    {
        IsQueueAlive = false;

        if (fault is not null)
        {
            Raise(new Banner("queue",
                $"The download queue stopped and this session cannot start more work: {fault.Message}",
                BannerKind.Error));
        }
    }

    public async Task ShutdownAsync()
    {
        if (IsShuttingDown) return;

        IsShuttingDown = true;

        // Stopped before the flush below, so the tick cannot race it and write twice.
        if (_saveTimer is { } saveTimer)
        {
            _saveTimer = null;
            await saveTimer.DisposeAsync();
        }

        // First: an unlocker operation cannot be cancelled, and exiting inside its File.Copy is
        // the damage this wait exists to prevent. Guarded because OnClosing is `async void`.
        try
        {
            await Unlocker.DrainAsync();
        }
        catch (Exception e)
        {
            Raise(new Banner("unlocker-error",
                $"The unlocker did not finish cleanly: {e.Message}", BannerKind.Error));
        }

        // OnClosing is `async void`, and FlushSettingsNowAsync catches only IOException and
        // UnauthorizedAccessException, so a serialiser fault would escape it as a crash on close.
        try
        {
            await FlushSettingsNowAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            Raise(new Banner("settings", $"Your settings could not be saved: {e.Message}", BannerKind.Warning));
        }

        // CancelAll, never Complete(): Complete() makes a blocked pack eligible to start and
        // releases the loop's signal, so the installer could still reach its journal marker and
        // leave a Partial install behind. DisposeAsync calls StopAcceptingWork itself anyway.
        //
        // The inner finally matters: CancelAll ends in cts.Cancel(), which runs cancellation
        // callbacks on this thread and rethrows them wrapped. In one try that throw would skip
        // disposal, and the loop would keep installing packs after the window closed.
        try
        {
            try
            {
                _services.Queue.CancelAll();
            }
            finally
            {
                await _services.Queue.DisposeAsync();
            }
        }
        catch (Exception e)
        {
            Raise(new Banner("queue",
                $"The download queue did not stop cleanly: {e.Message}", BannerKind.Error));
        }

        // Safe unguarded: ObserveQueueAsync catches the queue's fault and QueueBridge catches
        // its own, so each task always completes successfully.
        if (_queueRun is not null) await _queueRun;
        if (_bridgeRun is not null) await _bridgeRun;
    }
}
