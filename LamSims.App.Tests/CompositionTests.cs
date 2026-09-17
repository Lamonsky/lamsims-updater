using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Text.Json;
using LamSims.App;
using LamSims.App.ViewModels;
using LamSims.Core.Logging;
using LamSims.Core.Unlocking;

namespace LamSims.App.Tests;

public class CompositionTests
{
    [Fact(Timeout = 15000)]
    public async Task The_graph_builds_inside_the_root_it_is_given()
    {
        // The override must reach the DOWNLOAD directory too. DownloadPaths defaults to the
        // real user profile, so a graph that only redirects AppPaths writes .part files,
        // archives, quarantines and lock files into the developer's actual download folder.
        var root = Directory.CreateTempSubdirectory("lamsims-composition").FullName;

        try
        {
            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            Assert.StartsWith(root, services.Paths.Root, StringComparison.Ordinal);
            Assert.Equal(services.Paths.InstallStateDirectory, services.InstallState.Root);
            Assert.True(Directory.Exists(Path.Combine(root, "downloads")));

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task A_download_directory_that_cannot_be_created_banners_rather_than_killing_the_launch()
    {
        // The shipped case: the user puts downloads on a removable drive and unplugs it.
        // Composition runs from OnFrameworkInitializationCompleted BEFORE MainWindow exists and
        // Program.Main installs no handler, so throwing here means no window ever opens, and the
        // setting that caused it can only be changed in that window. PackQueue already guards the
        // same call so the queue can banner it; composition ran it first and unguarded.
        var root = Directory.CreateTempSubdirectory("lamsims-composition").FullName;

        try
        {
            // A file where the directory must go. Portable, and needs no permission change.
            var unusable = Path.Combine(root, "unplugged");
            File.WriteAllText(unusable, "");

            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(
                Path.Combine(root, "config", "settings.json"),
                $"{{\"downloadDirectory\":{JsonSerializer.Serialize(unusable)}}}");

            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            // The pair. "It did not throw" alone would also hold for a build that silently used
            // some other directory and never told the user their setting was ignored.
            Assert.NotNull(services.SettingsError);
            Assert.Contains(unusable, services.SettingsError);
            Assert.True(Directory.Exists(Path.Combine(root, "downloads")));

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task A_configuration_root_that_cannot_be_created_banners_rather_than_killing_the_launch()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-composition").FullName;

        try
        {
            // A file where AppPaths.Root must go.
            File.WriteAllText(Path.Combine(root, "config"), "");

            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            Assert.NotNull(services.SettingsError);
            Assert.Contains("config", services.SettingsError);

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The unlocker leg of the graph. Nothing else asserts it: every other test injects its own
    /// service, and a graph built with no backend at all passes the whole suite while shipping an
    /// unlocker that can never find a client. Both ids are pinned, and in order, because each
    /// backend answers IsSupported for one platform and a graph missing either one leaves that
    /// platform with no unlocker.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task The_graph_registers_both_unlocker_backends_in_order()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-composition-unlocker").FullName;

        try
        {
            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            // Both backends, in registration order. The Wine backend reports IsSupported false on
            // Windows, so registering it changes nothing there — but a graph missing it ships a
            // Linux build whose unlocker can never find a prefix.
            Assert.Equal(["windows-native", "wine-prefix"], services.Unlocker.BackendIds);
            Assert.IsType<LamSims.Core.Unlocking.WindowsUnlockerHost>(services.UnlockerHost);
            Assert.IsType<LamSims.Core.Unlocking.StaticUnlockerAssetSource>(services.UnlockerAssets);

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Pins the <c>MainViewModel</c>-side half of the wiring only: <c>Log</c> must be built over
    /// the exact <see cref="LogRelay"/> instance carried on the <see cref="AppServices"/> it was
    /// given — including after a test or <c>MainWindow</c> replaces other members with
    /// <c>services with { Dispatcher = … }</c> — rather than over a freshly constructed relay of
    /// its own. A <c>MainViewModel</c> that built its own would show an empty log forever no
    /// matter what Core wrote.
    ///
    /// It does NOT exercise the other half — that Composition hands the SAME relay to the seven
    /// Core constructors in the first place — because it writes directly through
    /// <c>services.Log</c> rather than through a real Core call. <see cref="EndToEndTests"/>'s
    /// <c>A_pack_downloads_installs_and_the_row_ends_up_installed</c> and
    /// <c>A_second_run_over_a_vouched_for_archive_never_reports_verifying</c> cover that half, by
    /// asserting on log lines that only <see cref="LamSims.Core.Downloading.SegmentedDownloader"/>,
    /// <see cref="LamSims.Core.Installing.ZipInstaller"/> and <see cref="LamSims.Core.PackWorkflow"/>
    /// can have written.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task The_view_models_log_is_built_over_the_relay_AppServices_carries()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-composition-log").FullName;

        try
        {
            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);
            var vm = new MainViewModel(services with { Dispatcher = new ImmediateDispatcher() });

            services.Log.Write(LogLine.Info("from core"));

            Assert.Contains(vm.Log.Lines, l => l.Text == "from core");

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The gap the three EndToEndTests-based assertions could not reach: they exercise a real
    /// CatalogLoader.ResolveAsync — Composition.Build gives services.Catalog the same relay it
    /// gives every other Core constructor, and ResolveAsync logs every failing candidate
    /// (CatalogLoader.cs:130) regardless of whether the failure is ultimately reported or passed
    /// over. A command-line candidate pointing at a path that does not exist is itself a failing
    /// candidate, so this needs no catalog file, no HTTP server, and no MainViewModel.StartAsync —
    /// just services.Catalog.ResolveAsync called directly, the same call MainViewModel's own
    /// _resolve default makes.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Composition_wires_the_relay_into_the_catalog_loader()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-composition-catalog-log").FullName;

        try
        {
            var missing = Path.Combine(root, "does-not-exist.json");
            var services = Composition.Build(commandLineCatalog: missing, overrideRoot: root);
            var vm = new MainViewModel(services with { Dispatcher = new ImmediateDispatcher() });

            await services.Catalog.ResolveAsync(
                services.CommandLineCatalog, services.Current.CatalogSource, CancellationToken.None);

            Assert.Contains(vm.Log.Lines, l => l.IsError && l.Text.Contains("Catalog load failed"));

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The three of Composition's seven relay wiring sites the hand-written tests above cannot
    /// reach: StaticUnlockerAssetSource, EaClientUnlockerBackend and WinePrefixUnlockerBackend
    /// were parked as untestable because driving them for real needs an EA client or a Wine
    /// prefix. That reasoning conflated driving the SERVICE with checking the WIRING — this
    /// checks only that Composition.Build handed each constructor the SAME LogRelay instance it
    /// gave everything else, by reflecting on each one's private `_log` field, which needs
    /// neither. It runs on any OS and guards all seven sites (the other four already have a
    /// hand-written test proving lines actually flow, which reflection cannot prove) plus any
    /// constructor added to the graph later.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Composition_wires_the_same_relay_into_every_service_that_takes_one()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-composition-relay-sweep").FullName;

        try
        {
            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            Assert.Same(services.Log, LogField(services.UnlockerAssets));

            var backendsField = typeof(UnlockerService).GetField(
                "_backends", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(backendsField);

            var backends = (IUnlockerBackend[])backendsField!.GetValue(services.Unlocker)!;

            // Both backends, not just however many happen to be registered: a sweep that silently
            // passed over an empty array would prove nothing about either one.
            Assert.Equal(2, backends.Length);

            foreach (var backend in backends)
            {
                Assert.Same(services.Log, LogField(backend));
            }

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Reads the private `_log` field every constructor in LogView design §3's table stores its
    /// sink under (`_log = log ?? NullLogSink.Instance`). Missing entirely — rather than merely
    /// holding a different sink — is reported by name instead of a null reference exception, so a
    /// renamed field fails this test loudly rather than as a mysterious NullReferenceException.
    /// </summary>
    private static object LogField(object service)
    {
        var field = service.GetType().GetField("_log", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(field is not null, $"{service.GetType().Name} has no private '_log' field");
        return field!.GetValue(service)!;
    }
}
