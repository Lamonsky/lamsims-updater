using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using LamSims.App;
using LamSims.App.ViewModels;
using LamSims.Core.Downloading;

namespace LamSims.App.Tests;

public class EndToEndTests
{
    private static byte[] TestArchive(params (string Name, int Size)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, size) in entries)
            {
                // Stored, not deflated, with a name-derived seed: two runs must plan the same
                // number of chunks, and string.GetHashCode is randomised per process.
                var seed = name.Aggregate(17, (a, c) => a * 31 + c);
                var bytes = new byte[size];
                new Random(seed).NextBytes(bytes);

                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private static PackEntry Entry(byte[] archive, TestFileServer server) =>
        new("EP01", "Get to Work", PackType.Expansion, archive.Length, null,
            Sha256Of(archive), [server.FileUrl], ["EP01"]);

    /// <summary>
    /// A view model over the real engine. The resolve seam rather than a catalog file, because
    /// StartAsync loads the catalog and would rebuild the row list out from under the test; and
    /// the immediate dispatcher rather than Avalonia's, because there is no UI thread here.
    /// </summary>
    private static MainViewModel Shell(string root, string game, PackEntry entry)
    {
        var services = Composition.Build(
            commandLineCatalog: null, overrideRoot: root, delays: new FakeDelayProvider());

        var vm = new MainViewModel(
            services with { Dispatcher = new ImmediateDispatcher() },
            resolve: _ => Task.FromResult(
                new CatalogResolution(
                    CatalogStatus.Loaded,
                    new CatalogLoadResult(new Catalog(1, null, [entry]), []),
                    new CatalogSource(CatalogSourceKind.Settings, "test"), null, null)));

        vm.GameDirectory = game;
        return vm;
    }

    [Fact(Timeout = 15000)]
    public async Task A_pack_downloads_installs_and_the_row_ends_up_installed()
    {
        // The whole chain: a real SegmentedDownloader over loopback, a real ZipInstaller writing
        // a real journal marker, a real PackQueue behind the controller, the bridge, the rows,
        // and the rescan that turns Completed into what the marker says.
        var root = Directory.CreateTempSubdirectory("lamsims-e2e").FullName;
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        MainViewModel? vm = null;

        try
        {
            var archive = TestArchive(("EP01/data.package", 400_000));
            await using var server = await TestFileServer.StartAsync(archive);

            vm = Shell(root, game, Entry(archive, server));
            await vm.StartAsync(CancellationToken.None);

            vm.Rows[0].IsChecked = true;
            vm.AddSelectedCommand.Execute(null);

            // Waits on both InstallState and the overlay. ApplyScan assigns InstallState
            // several statements before it drops the overlay, so a wait on the state alone can
            // return inside that window and the three assertions below become a coin flip.
            await WaitUntil(() =>
                vm.Rows[0].InstallState == PackInstallState.Installed && vm.Rows[0].QueueState is null);

            Assert.Equal("Installed", vm.Rows[0].StatusText);
            Assert.Null(vm.Rows[0].QueueState);      // the rescan cleared the terminal overlay
            Assert.Null(vm.Rows[0].CurrentEntry);    // and the sticky entry is blanked
            Assert.Equal(0, vm.PendingCount);

            // The end-to-end proof CompositionTests.cs's relay test cannot give: these two lines
            // exist nowhere but inside SegmentedDownloader.DownloadAsync and
            // ZipInstaller.InstallAsync (LamSims.Core), so their presence in vm.Log.Lines means
            // the ILogSink Composition.Build handed those two constructors really is the same
            // LogRelay this MainViewModel drains, not a look-alike neither side ever writes to.
            Assert.Contains(vm.Log.Lines, l => l.Text.StartsWith("Fetching "));
            Assert.Contains(vm.Log.Lines, l => l.Code == "EP01" && l.Text.StartsWith("Installed EP01"));
        }
        finally
        {
            // In the finally: a failed assertion above must still stop the real queue this test
            // started before the outer directory delete runs out from under it. The shutdown is
            // swallowed rather than awaited, so a shutdown fault cannot replace (and hide) a
            // genuine assertion failure already unwinding through here.
            if (vm is not null)
            {
                try { await vm.ShutdownAsync(); }
                catch { /* the assertion failure this finally protects takes priority */ }
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task A_second_run_over_a_vouched_for_archive_never_reports_verifying()
    {
        // A digest sidecar matching the archive's digest, length and mtime means
        // PackWorkflow trusts it and hashes nothing (PackWorkflow.cs:118-126). So Verifying is
        // not a phase every run passes through, and a UI that assumed otherwise would be built
        // against a ladder that does not exist.
        //
        // Seeded directly rather than via a first run-and-install: ZipInstaller deletes the
        // archive it consumed on a successful install (ZipInstaller.cs:240) and PackWorkflow
        // deletes the digest sidecar with it (PackWorkflow.cs:79-81), so a genuine two-run shape
        // would find nothing on disk for the second run to trust and would exit ClassifyAsync at
        // its first rung (NeedsDownload), reaching Installing without ever passing through
        // Verifying regardless of whether the guard this test exists for is present. Seeding the
        // archive and a matching record directly is what actually reaches the trusted rung.
        var root = Directory.CreateTempSubdirectory("lamsims-e2e-trust").FullName;
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        MainViewModel? vm = null;

        try
        {
            var archive = TestArchive(("EP01/data.package", 400_000));
            await using var server = await TestFileServer.StartAsync(archive);
            var entry = Entry(archive, server);

            // Composition.Build(overrideRoot:) puts the download root at "<root>/downloads"
            // when no DownloadDirectory is configured. That is the same DownloadPaths PackWorkflow
            // builds its own ArchiveDigestStore over (PackWorkflow.cs:44), so seeding through
            // this exact path is what the workflow actually reads.
            var downloads = new DownloadPaths(Path.Combine(root, "downloads"));
            downloads.EnsureCreated();
            var archivePath = downloads.ArchiveFile("EP01");
            File.WriteAllBytes(archivePath, archive);
            await new ArchiveDigestStore(downloads).RecordAsync("EP01", archivePath, Sha256Of(archive));

            vm = Shell(root, game, entry);
            await vm.StartAsync(CancellationToken.None);

            // ConcurrentBag, not List: PropertyChanged fires from whatever thread is pumping
            // QueueBridge's channel loop, concurrently with this thread's WaitUntil poll and the
            // assertions below. List<T>'s enumerator throws InvalidOperationException under a
            // concurrent Add; ConcurrentBag's does not, and unordered membership is all Contains
            // and DoesNotContain ever need here.
            var statuses = new System.Collections.Concurrent.ConcurrentBag<string>();
            var row = vm.Rows[0];
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(row.StatusText)) statuses.Add(row.StatusText);
            };

            // Nothing was installed beforehand, so the row scans as NotInstalled and is
            // checkable without ForceCheckable.
            row.IsChecked = true;
            vm.AddSelectedCommand.Execute(null);

            await WaitUntil(() => statuses.Contains("Installed"));

            Assert.DoesNotContain(statuses, t => t.StartsWith("Verifying"));

            // The pair: trusted means no hash AND no fetch. Asserting the absence of
            // "Verifying" alone passes even if the ladder regressed to re-downloading every
            // time, since a fresh download never reports Verifying either.
            Assert.Equal(0, server.RequestCount);

            // PackWorkflow.ClassifyAsync is the only place that writes this exact line
            // (LamSims.Core/PackWorkflow.cs:132-133), and RequestCount == 0 above rules out
            // SegmentedDownloader ever having run, so this line can only have reached vm.Log
            // through the relay Composition.Build gave PackWorkflow directly.
            Assert.Contains(vm.Log.Lines,
                l => l.Code == "EP01" && l.Text.Contains("Trusting the existing archive"));
        }
        finally
        {
            if (vm is not null)
            {
                try { await vm.ShutdownAsync(); }
                catch { /* the assertion failure this finally protects takes priority */ }
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task A_cancelled_download_does_not_leave_the_row_installed()
    {
        // StallAfterBytes is what makes this deterministic: the server stops sending partway
        // through each chunk, so the transfer physically cannot finish and a cancel cannot lose a
        // race against it. AtStall is what makes the CANCEL deterministic — it fires on the
        // server's own thread at the moment the sending stops, so the wait below ends only once a
        // transfer is genuinely in flight. Waiting for Downloading instead is not the same thing:
        // PackWorkflow reports it before the transfer begins, and on a slow runner the cancel
        // then landed before there was anything to cancel. Both halves are asserted because the
        // scanner produces Partial by two different rules and Partial alone cannot tell them
        // apart.
        var root = Directory.CreateTempSubdirectory("lamsims-e2e-cancel").FullName;
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        MainViewModel? vm = null;

        try
        {
            var archive = TestArchive(("EP01/data.package", 400_000));
            var stalled = 0;
            using var release = new CancellationTokenSource();

            await using var server = await TestFileServer.StartAsync(
                archive,
                new TestFileServerOptions
                {
                    // Above the one byte the downloader's range probe asks for, so the probe is
                    // answered in full and only the chunk requests stall. A probe left stalling
                    // is one the client counts as complete and never aborts, and disposing the
                    // server then waits out the host's whole shutdown timeout.
                    StallAfterBytes = 1024,
                    AtStall = () => Interlocked.Exchange(ref stalled, 1),
                    ReleaseStall = release.Token,
                });

            // In its own finally, inside the server's scope: the server is disposed as this
            // block leaves, and disposing it with a handler still stalled costs the host's whole
            // shutdown timeout. On the failing path that delay outlives the [Fact] timeout and
            // replaces the real diagnosis with xunit's bare one.
            try
            {
                vm = Shell(root, game, Entry(archive, server));
                await vm.StartAsync(CancellationToken.None);

                vm.Rows[0].IsChecked = true;
                vm.AddSelectedCommand.Execute(null);

                await WaitUntil(() => Volatile.Read(ref stalled) == 1);

                // From this thread rather than from inside AtStall: cancelling there runs the
                // cancellation callbacks inline on the request's own thread, inside the handler
                // being torn down.
                vm.Rows[0].CancelCommand.Execute(null);

                await WaitUntil(() => vm.Rows[0].QueueState == QueueItemState.Cancelled);

                // Scan explicitly. The automatic rescan fires only on Completed, so a cancelled
                // row keeps the queue's word for it until the next scan, which is why waiting for
                // the overlay to clear here would hang. Scan() is synchronous, so nothing needs
                // to be awaited after it.
                vm.ScanCommand.Execute(null);

                Assert.Null(vm.Rows[0].QueueState);
                Assert.NotEqual(PackInstallState.Installed, vm.Rows[0].InstallState);
                Assert.True(
                    vm.Rows[0].InstallState == PackInstallState.NotInstalled
                        || vm.Rows[0].MissingDirs.Count > 0,
                    $"expected NotInstalled or a named missing directory, got "
                        + $"{vm.Rows[0].InstallState} with {vm.Rows[0].MissingDirs.Count} missing");
            }
            finally
            {
                release.Cancel();
            }
        }
        finally
        {
            if (vm is not null)
            {
                try { await vm.ShutdownAsync(); }
                catch { /* the assertion failure this finally protects takes priority */ }
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
