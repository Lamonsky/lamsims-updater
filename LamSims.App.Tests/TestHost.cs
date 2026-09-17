using LamSims.Core.Catalogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LamSims.App;
using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.App.Tests;

public sealed class TestHost : IDisposable
{
    public required string Root { get; init; }
    public required string GameDirectory { get; init; }
    public required AppPaths Paths { get; init; }
    public required IQueueController Controller { get; init; }
    public required FakePickers Pickers { get; init; }
    public required FakeClipboard Clipboard { get; init; }
    public required FakeClock Clock { get; init; }

    /// <summary>The two the view model mutates in place; a test asserts on them, never rebuilds them.</summary>
    public required DownloadPaths DownloadPaths { get; init; }

    public required DownloadOptions DownloadOptions { get; init; }
    public required List<AppSettings> Saved { get; init; }

    /// <summary>
    /// The recording queue, for the tests that assert its call sequence. A test that passed its
    /// own <c>queue</c> has nothing to record and must not ask: the cast throws rather than
    /// silently handing back a queue the view model never touched.
    /// </summary>
    public RecordingQueue Queue => (RecordingQueue)Controller;

    public static MainViewModel ViewModel(
        out TestHost host,
        Func<CancellationToken, Task<CatalogResolution>>? resolve = null,
        Func<CatalogSource, CancellationToken, Task<CatalogResolution>>? load = null,
        Func<AppSettings, CancellationToken, Task>? save = null,
        string? settingsError = null,
        Action<AppSettings>? seed = null,
        IQueueController? queue = null,
        UnlockerService? unlockerService = null,
        IUnlockerHost? unlockerHost = null,
        IUnlockerAssetSource? unlockerAssets = null,
        UnlockerNotes? unlockerNotes = null)
    {
        var root = Directory.CreateTempSubdirectory("lamsims-vm").FullName;
        var game = Path.Combine(root, "game");

        // Game/Bin, so the fixture is a directory GameRootCheck reads as an installation. Without
        // it every scan here would raise the layout warning, and a test asserting on banners would
        // be asserting against a game folder no user would have chosen.
        Directory.CreateDirectory(Path.Combine(game, "Game", "Bin"));

        var paths = new AppPaths(Path.Combine(root, "config"));
        paths.EnsureCreated();

        // Under the temp root for the same reason Composition redirects it: a view model that
        // retargets the real default would create directories in the developer's profile.
        var downloadPaths = new DownloadPaths(Path.Combine(root, "downloads"));
        downloadPaths.EnsureCreated();
        var downloadOptions = new DownloadOptions();

        var controller = queue ?? new RecordingQueue();
        var pickers = new FakePickers();
        var clipboard = new FakeClipboard();
        var clock = new FakeClock();
        var saved = new List<AppSettings>();

        // Seeded before the view model is constructed: its constructor copies these into the
        // backing fields directly, so a change made afterwards would never be read.
        var settings = new AppSettings();
        seed?.Invoke(settings);

        var services = new AppServices(
            paths,
            new SettingsStore(paths),
            settings,
            settingsError,
            new CatalogLoader(new HttpClient(), paths),
            new InstallStateStore(paths.InstallStateDirectory),
            controller,
            new ImmediateDispatcher(),
            pickers,
            clipboard,
            clock,
            null,
            // A service with no backends reports IsSupported false, so the region hides itself
            // and every pre-existing test is unaffected by this phase. A wiring test overrides
            // one or all three to drive UnlockerViewModel through a real MainViewModel.
            unlockerService ?? new UnlockerService([]),
            unlockerHost ?? new FakeUnlockerHost { IsAvailable = false },
            unlockerAssets ?? new StubUnlockerAssets(),
            unlockerNotes ?? new UnlockerNotes(),
            downloadPaths,
            downloadOptions,
            new OrphanCleaner(downloadPaths),
            new LogRelay(clock));

        host = new TestHost
        {
            Root = root,
            GameDirectory = game,
            Paths = paths,
            Controller = controller,
            Pickers = pickers,
            Clipboard = clipboard,
            Clock = clock,
            DownloadPaths = downloadPaths,
            DownloadOptions = downloadOptions,
            Saved = saved,
        };

        var recordingSave = save ?? ((settings, _) =>
        {
            saved.Add(settings);
            return Task.CompletedTask;
        });

        return new MainViewModel(services, resolve, load, recordingSave);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakePickers : IPickerService
{
    public string? NextFolder { get; set; }
    public string? NextFile { get; set; }

    public Task<string?> PickFolderAsync(string title, string? startAt) => Task.FromResult(NextFolder);

    public Task<string?> PickFileAsync(string title, string? startAt) => Task.FromResult(NextFile);
}

/// <summary>Records every string it was asked to copy, so a test can assert on what Copy sent.</summary>
public sealed class FakeClipboard : IClipboardService
{
    public List<string> Copied { get; } = new();

    public Task SetTextAsync(string text)
    {
        Copied.Add(text);
        return Task.CompletedTask;
    }
}
