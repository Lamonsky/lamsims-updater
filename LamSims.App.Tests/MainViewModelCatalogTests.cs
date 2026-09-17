using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class MainViewModelCatalogTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    [Fact(Timeout = 15000)]
    public async Task An_empty_resolution_is_an_empty_state_not_an_error()
    {
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(CatalogStatus.Empty, null, null, null, null)));
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Empty(vm.Rows);
        Assert.Equal("No catalog selected.", vm.EmptyStateMessage);
        Assert.Empty(vm.Banners);
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_resolution_banners_the_error_and_offers_the_cached_copy()
    {
        var cached = new CatalogSource(CatalogSourceKind.Cache, "/tmp/catalog.cache.json");
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(CatalogStatus.Failed, null, null, "the mirror returned 500", cached)));
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Contains(vm.Banners, b => b.Kind == BannerKind.Error && b.Text.Contains("the mirror returned 500"));
        Assert.True(vm.UseCachedCatalogCommand.CanExecute(null));
    }

    [Fact(Timeout = 15000)]
    public async Task The_cached_copy_is_loaded_through_LoadAsync_not_by_resolving_again()
    {
        // Re-resolving walks the same candidate chain and fails against the same source.
        // LoadAsync is the only entry point core gives for loading a named source, and core
        // documents it as existing for exactly this flow.
        var cached = new CatalogSource(CatalogSourceKind.Cache, "/tmp/catalog.cache.json");
        var loaded = new List<CatalogSource>();
        var resolves = 0;

        var vm = TestHost.ViewModel(out var host,
            resolve: _ =>
            {
                resolves++;
                return Task.FromResult(new CatalogResolution(CatalogStatus.Failed, null, null, "down", cached));
            },
            load: (source, _) =>
            {
                loaded.Add(source);
                return Task.FromResult(new CatalogResolution(
                    CatalogStatus.Loaded,
                    new CatalogLoadResult(new Catalog(1, null, [Packs.Entry()]), []),
                    source, null, null));
            });
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);
        await vm.UseCachedCatalogAsync(CancellationToken.None);

        Assert.Equal([cached], loaded);
        Assert.Equal(1, resolves);
        Assert.Single(vm.Rows);
        Assert.DoesNotContain(vm.Banners, b => b.Id == "catalog");
    }

    [Fact(Timeout = 15000)]
    public async Task A_loaded_catalog_sweeps_partials_for_packs_it_no_longer_lists()
    {
        // OrphanCleaner shipped unreferenced: nothing in the app ever called it, so a .part file
        // for a de-listed pack was unreachable from inside the app and never reclaimed. The pair:
        // the stale partial goes AND the listed pack's partial stays, which is what proves the
        // sweep is driven by the catalog's codes rather than deleting everything it finds.
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(
                CatalogStatus.Loaded,
                new CatalogLoadResult(new Catalog(1, null, [Packs.Entry()]), []),
                new CatalogSource(CatalogSourceKind.Settings, "/tmp/catalog.json"), null, null)));
        using var _h = host;

        var listed = host.DownloadPaths.PartFile("EP01");
        var delisted = host.DownloadPaths.PartFile("EP99");
        File.WriteAllText(listed, "");
        File.WriteAllText(delisted, "");

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.False(File.Exists(delisted));
        Assert.True(File.Exists(listed));
        Assert.Equal(1, vm.OrphansSwept);
    }

    /// <summary>
    /// The asset-temp sweep hangs off StartAsync, not off a catalog load, because it is only safe
    /// before anything can fetch. Both halves: the temp goes AND a de-listed pack's partial stays,
    /// which is what proves this is the temp sweep and not the catalog-driven one running early —
    /// the catalog never loads here, so the orphan sweep cannot have run at all.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Starting_sweeps_an_asset_temp_without_waiting_for_a_catalog()
    {
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(
                CatalogStatus.Empty, null,
                new CatalogSource(CatalogSourceKind.Settings, ""), null, null)));
        using var _h = host;

        host.DownloadPaths.EnsureCreated();
        var temp = host.DownloadPaths.UnlockerAssetFile("ea_app_version.dll") + ".a1b2c3d4.incoming";
        var partial = host.DownloadPaths.PartFile("EP99");
        File.WriteAllText(temp, "half a dll");
        File.WriteAllText(partial, "");

        await vm.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(temp));
        Assert.Equal(1, vm.AssetTempsSwept);
        Assert.True(File.Exists(partial));

        await vm.ShutdownAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Rejected_entries_are_one_banner_and_do_not_block_the_rest()
    {
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(
                CatalogStatus.Loaded,
                new CatalogLoadResult(
                    new Catalog(1, null, [Packs.Entry()]),
                    [new RejectedEntry("SP99", "its size was negative")]),
                new CatalogSource(CatalogSourceKind.Settings, "/tmp/catalog.json"), null, null)));
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Single(vm.Rows);
        var banner = Assert.Single(vm.Banners);
        Assert.Equal(BannerKind.Warning, banner.Kind);
        Assert.Contains("SP99", banner.Text);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancellation_during_resolution_is_not_reported_as_a_catalog_error()
    {
        // ResolveAsync propagates the caller's cancellation as OperationCanceledException
        // rather than folding it into Failed. A call site that does not catch it turns closing
        // the window during startup into a spurious error.
        var vm = TestHost.ViewModel(out var host,
            resolve: _ => throw new OperationCanceledException());
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Empty(vm.Banners);
    }

    [Fact]
    public void A_settings_load_error_is_bannered_at_construction()
    {
        var vm = TestHost.ViewModel(out var host, settingsError: "settings.json was not valid JSON");
        using var _h = host;

        Assert.Contains(vm.Banners, b => b.Id == "settings-load" && b.Text.Contains("not valid JSON"));
    }

    [Fact(Timeout = 15000)]
    public async Task Changing_the_catalog_rescans_so_an_installed_pack_does_not_read_Not_installed()
    {
        // Apply rebuilds every row, and a fresh row knows nothing about what is on disk.
        // Startup hid this because StartAsync scans separately, but ChangeCatalogAsync and
        // UseCachedCatalogAsync reach the rows ONLY through Apply. Without the rescan a
        // first-run user who presses Change and picks their catalog is shown every already
        // installed pack as "Not installed" with a live checkbox, and Add re-downloads them.
        //
        // A row rebuilt from the catalog also reads NotInstalled, so InstalledUnverified (a
        // value only ApplyScan can produce) is what discriminates, and ScanCount pins that the
        // scan ran at all rather than some other route setting the state.
        var vm = TestHost.ViewModel(out var host,
            load: (source, _) => Task.FromResult(new CatalogResolution(
                CatalogStatus.Loaded,
                new CatalogLoadResult(new Catalog(1, null, [Packs.Entry()]), []),
                source, null, null)));
        using var _h = host;

        vm.GameDirectory = host.GameDirectory;
        Directory.CreateDirectory(Path.Combine(host.GameDirectory, "EP01"));
        host.Pickers.NextFile = Path.Combine(host.Root, "catalog.json");

        await vm.ChangeCatalogCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(1, vm.ScanCount);
        Assert.Equal(PackInstallState.InstalledUnverified, row.InstallState);
        Assert.Equal("Installed (not verified by this tool)", row.StatusText);
    }

    [Fact]
    public void Changing_the_catalog_is_refused_while_work_is_in_the_queue()
    {
        // Rebuilding rows under a live queue orphans an in-flight pack's row while it keeps its
        // lock and its bandwidth. Requiring an empty queue removes the whole class.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        Assert.False(vm.ChangeCatalogCommand.CanExecute(null));

        vm.ApplyUpdate(new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]));
        Assert.True(vm.ChangeCatalogCommand.CanExecute(null));
    }

    /// <summary>
    /// Records what each source-applying run loaded and what it saved. The two are asserted
    /// together: the view model writes the setting and loads the source separately, so a test
    /// that watched only one would stay green while the other silently kept the old value.
    /// </summary>
    private static MainViewModel TypedSourceViewModel(
        out TestHost host, out List<CatalogSource> loaded, out List<string?> saved)
    {
        var loadedSources = new List<CatalogSource>();
        var savedSources = new List<string?>();

        var vm = TestHost.ViewModel(out host,
            load: (source, _) =>
            {
                loadedSources.Add(source);
                return Task.FromResult(new CatalogResolution(
                    CatalogStatus.Loaded,
                    new CatalogLoadResult(new Catalog(1, null, [Packs.Entry()]), []),
                    source, null, null));
            },
            save: (settings, _) =>
            {
                savedSources.Add(settings.CatalogSource);
                return Task.CompletedTask;
            });

        loaded = loadedSources;
        saved = savedSources;
        return vm;
    }

    [Fact(Timeout = 15000)]
    public async Task A_typed_url_is_loaded_as_a_remote_source()
    {
        var vm = TypedSourceViewModel(out var host, out var loaded, out var saved);
        using var _h = host;

        vm.CatalogInput = "https://mirror.example.invalid/catalog.json";
        await vm.ApplyCatalogSourceCommand.ExecuteAsync(null);

        var source = Assert.Single(loaded);
        Assert.Equal("https://mirror.example.invalid/catalog.json", source.Location);
        Assert.True(source.IsRemote);
        Assert.Equal(["https://mirror.example.invalid/catalog.json"], saved);
        Assert.Single(vm.Rows);
    }

    [Fact(Timeout = 15000)]
    public async Task A_typed_local_path_is_loaded_as_a_local_source()
    {
        // The same box takes both. IsRemote is what routes the load, so a path that arrived
        // looking remote would be fetched over HTTP instead of read from disk.
        var vm = TypedSourceViewModel(out var host, out var loaded, out var saved);
        using var _h = host;

        var path = Path.Combine(host.Root, "catalog.json");
        vm.CatalogInput = path;
        await vm.ApplyCatalogSourceCommand.ExecuteAsync(null);

        var source = Assert.Single(loaded);
        Assert.Equal(path, source.Location);
        Assert.False(source.IsRemote);
        Assert.Equal([path], saved);
    }

    [Fact(Timeout = 15000)]
    public async Task Surrounding_whitespace_is_trimmed_before_the_source_is_used()
    {
        // A URL pasted from a browser or a chat client routinely arrives with a trailing
        // newline, and neither the file reader nor Uri.TryCreate forgives one.
        var vm = TypedSourceViewModel(out var host, out var loaded, out var saved);
        using var _h = host;

        vm.CatalogInput = "  https://mirror.example.invalid/catalog.json\n";
        await vm.ApplyCatalogSourceCommand.ExecuteAsync(null);

        Assert.Equal("https://mirror.example.invalid/catalog.json", Assert.Single(loaded).Location);
        Assert.Equal(["https://mirror.example.invalid/catalog.json"], saved);
        Assert.Equal("https://mirror.example.invalid/catalog.json", vm.CatalogInput);
    }

    [Fact(Timeout = 15000)]
    public async Task Emptying_the_box_clears_the_setting_and_returns_to_the_empty_state()
    {
        // Clearing is the only way back to "no catalog" once one has been chosen; without it a
        // typo in the setting is permanent, because every launch resolves it and fails.
        var vm = TypedSourceViewModel(out var host, out var loaded, out var saved);
        using var _h = host;

        vm.CatalogInput = "https://mirror.example.invalid/catalog.json";
        await vm.ApplyCatalogSourceCommand.ExecuteAsync(null);

        vm.CatalogInput = "   ";
        await vm.ApplyCatalogSourceCommand.ExecuteAsync(null);

        Assert.Single(loaded);            // the blank was not handed to the loader
        Assert.Equal([null], saved[1..]);
        Assert.Empty(vm.Rows);
        Assert.Equal("No catalog selected.", vm.EmptyStateMessage);
    }

    [Fact(Timeout = 15000)]
    public async Task Browsing_for_a_catalog_file_fills_the_box()
    {
        // The box and the browse button name one setting. A pick that left the box reading the
        // previous source would be undone by the next Load the user pressed.
        var vm = TypedSourceViewModel(out var host, out _, out _);
        using var _h = host;

        var path = Path.Combine(host.Root, "picked.json");
        host.Pickers.NextFile = path;
        await vm.ChangeCatalogCommand.ExecuteAsync(null);

        Assert.Equal(path, vm.CatalogInput);
    }

    [Fact]
    public void Applying_a_typed_catalog_is_refused_while_work_is_in_the_queue()
    {
        // Same reason Change is refused: Apply rebuilds every row out from under a live pack.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        Assert.False(vm.ApplyCatalogSourceCommand.CanExecute(null));

        vm.ApplyUpdate(new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]));
        Assert.True(vm.ApplyCatalogSourceCommand.CanExecute(null));
    }

    [Fact]
    public void The_box_opens_showing_the_source_the_settings_already_name()
    {
        var vm = TestHost.ViewModel(out var host, seed: s => s.CatalogSource = "/srv/catalog.json");
        using var _h = host;

        Assert.Equal("/srv/catalog.json", vm.CatalogInput);
    }
}
