using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LamSims.App.Services;
using LamSims.Core;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Queueing;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.App;

public static class Composition
{
    /// <summary>
    /// Built once, from the settings as they are at launch. DownloadDirectory and Connections
    /// only need their starting values here. Both apply live afterwards, through
    /// DownloadPaths.Retarget and DownloadOptions.Connections.
    /// </summary>
    /// <param name="commandLineCatalog">The catalog named on the command line, if any.</param>
    /// <param name="overrideRoot">
    /// Redirects BOTH roots (the configuration root and, when no download directory is
    /// configured, the download root) so a test cannot write archives, quarantines or lock
    /// files into the developer's real profile. Tests pass one; production passes none.
    /// </param>
    /// <param name="delays">
    /// Exists so an end-to-end test cannot sleep for real time on a retry. Production passes none.
    /// </param>
    public static AppServices Build(
        string? commandLineCatalog, string? overrideRoot = null, IDelayProvider? delays = null)
    {
        // Every directory this method creates is reported rather than thrown. Build runs from
        // OnFrameworkInitializationCompleted before MainWindow exists and Program.Main installs no
        // handler, so an exception here means the application never opens a window, including the
        // window holding the setting that caused it.
        var faults = new List<string>();

        var paths = new AppPaths(overrideRoot is null ? null : Path.Combine(overrideRoot, "config"));
        TryCreate(paths.Root, paths.EnsureCreated, faults);

        var settingsStore = new SettingsStore(paths);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;

        var options = settings.ToDownloadOptions();

        // The container is held alongside the client because GoFileResolver has to write into the
        // same one the handler reads: a handler's container cannot be recovered from an HttpClient.
        var (http, cookies) = HttpFactory.CreateWithCookies();

        // The composition's own default, which a test's overrideRoot redirects; the setting is
        // the user's choice layered on top of it.
        var defaultDownloadRoot = overrideRoot is null ? null : Path.Combine(overrideRoot, "downloads");

        // Constructed on the default and then retargeted, rather than constructed on the setting:
        // DownloadPaths.Retarget(null) returns to the root it was built with, so this is what
        // makes clearing the setting later resolve to the default instead of to the cleared value.
        var downloadPaths = new DownloadPaths(defaultDownloadRoot);

        // PackLock does not create the download root and throws DirectoryNotFoundException when
        // it is missing, so it is created here rather than being left to the queue's first run.
        // A configured directory that cannot be created (a removable drive that is not plugged
        // in) falls back to the default, because the alternative is not starting at all.
        if (settings.DownloadDirectory is not { } configured
            || !TryCreate(configured, () => downloadPaths.Retarget(configured), faults))
        {
            TryCreate(downloadPaths.Root, downloadPaths.EnsureCreated, faults);
        }

        var installState = new InstallStateStore(paths.InstallStateDirectory);

        var effectiveDelays = delays ?? new SystemDelayProvider();
        var clock = new SystemClock();

        // The relay Core writes into and MainViewModel's LogViewModel later drains. Built here,
        // with no dispatcher, for the same reason LogRelay's own doc gives: it must outlive the
        // Dispatcher/Pickers members MainWindow and the tests replace with `services with { … }`
        // after Build returns.
        var log = new LogRelay(clock);

        var downloader = new SegmentedDownloader(
            http, downloadPaths, options, RetryOptions.Default, effectiveDelays, log);
        var installer = new ZipInstaller(installState, log: log);
        var workflow = new PackWorkflow(
            downloader, installer, downloadPaths, log,
            new GoFileResolver(http, cookies, log: log));
        var queue = new PackQueue(workflow, downloadPaths);

        // The unlocker must not create its own HttpClient: it fetches over the same one the
        // catalog and the queue use, so a test that redirects `http` redirects it too.
        var unlockerHost = new WindowsUnlockerHost();
        var unlockerAssets = new StaticUnlockerAssetSource(http, downloadPaths, log: log);
        var unlockerNotes = new UnlockerNotes();

        // Both backends are registered and each reports IsSupported for its own platform, so the
        // service asks only the one that can work here.
        var unlockerBackend = new EaClientUnlockerBackend(
            unlockerHost, new UnlockerPaths(), paths, effectiveDelays, log: log);

        // The home directory and both XDG values are read HERE, at the edge, and injected: nothing
        // under Unlocking/Wine calls Environment, which is what makes discovery testable. The
        // prefix setting is passed as a callback rather than a value so a change applies live.
        var wineBackend = new WinePrefixUnlockerBackend(
            new LauncherHomes(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolderOption.DoNotVerify),
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetEnvironmentVariable("WINEPREFIX")),
            paths, effectiveDelays, new WineProcesses(), Environment.UserName,
            () => settings.WinePrefix, unlockerNotes, log);

        var unlockerService = new UnlockerService([unlockerBackend, wineBackend]);

        return new AppServices(
            paths,
            settingsStore,
            settings,
            Combine(loaded.Error, faults),
            new CatalogLoader(http, paths, log: log),
            installState,
            new PackQueueController(queue),
            new AvaloniaUiDispatcher(),
            NullPickers.Instance,      // the window replaces this; it owns the TopLevel
            NullClipboard.Instance,    // same reason: the window replaces this too
            clock,
            commandLineCatalog,
            unlockerService,
            unlockerHost,
            unlockerAssets,
            unlockerNotes,
            downloadPaths,
            options,
            new OrphanCleaner(downloadPaths),
            log);
    }

    private static bool TryCreate(string root, Action create, List<string> faults)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            faults.Add($"'{root}' could not be created: {e.Message}");
            return false;
        }
    }

    private static string? Combine(string? settingsError, List<string> faults)
    {
        var all = settingsError is null ? faults : faults.Prepend(settingsError).ToList();

        return all.Count == 0 ? null : string.Join(" ", all);
    }
}

/// <summary>Stands in until the window exists; every method reports "the user cancelled".</summary>
internal sealed class NullPickers : IPickerService
{
    public static readonly NullPickers Instance = new();

    public Task<string?> PickFolderAsync(string title, string? startAt) => Task.FromResult<string?>(null);

    public Task<string?> PickFileAsync(string title, string? startAt) => Task.FromResult<string?>(null);
}

/// <summary>Stands in until the window exists; a copy attempted before then is a no-op.</summary>
internal sealed class NullClipboard : IClipboardService
{
    public static readonly NullClipboard Instance = new();

    public Task SetTextAsync(string text) => Task.CompletedTask;
}
