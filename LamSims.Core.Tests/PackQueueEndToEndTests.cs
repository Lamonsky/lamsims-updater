using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.IO.Compression;
using System.Security.Cryptography;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

/// <summary>
/// The queue against the real engine. The other queue files drive <c>ScriptedRunner</c>, which
/// covers the loop's own logic but not the loop together with <see cref="PackWorkflow"/>,
/// <see cref="SegmentedDownloader"/> and <see cref="ZipInstaller"/>: a pause mid-download must
/// leave the <c>.part</c> and its sidecar intact, and a cancel mid-install must leave
/// <see cref="InstallScanner"/> reporting <see cref="PackInstallState.Partial"/>.
///
/// The timeout is 30 s rather than the 15 s elsewhere, because these tests do real HTTP over
/// loopback and a real multi-megabyte extract.
///
/// The updates channel is <c>SingleReader = true</c>, so a test may not run
/// <see cref="UpdateWatcher"/> and its own <c>await foreach</c>. Each test below therefore has one
/// reader task that issues its control and completes a <see cref="TaskCompletionSource"/> when it
/// sees what the test waits for; there is no <c>queue.Snapshot()</c> to poll, and polling would
/// sleep for real time.
/// </summary>
public class PackQueueEndToEndTests
{
    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// A zip in memory whose size is predictable: stored, not deflated, with pseudo-random
    /// entry bytes.
    ///
    /// <see cref="ZipBuilder"/> cannot be used here: it writes to a path, takes <em>string</em>
    /// content and deflates it, so a 600 KB entry of repeated characters arrives as about a
    /// kilobyte, collapsing the archive to one chunk.
    ///
    /// The seed comes from the entry name's characters rather than <c>string.GetHashCode</c>,
    /// which is randomised per process, so two runs plan the same number of chunks.
    /// </summary>
    private static byte[] TestArchive(params (string Name, int Size)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, size) in entries)
            {
                var seed = name.Aggregate(17, (a, c) => a * 31 + c);
                var bytes = new byte[size];
                new Random(seed).NextBytes(bytes);

                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    private sealed record Rig(
        PackQueue Queue, PackEntry Pack, string GameDir, DownloadPaths Paths, InstallStateStore State);

    /// <summary>
    /// The real downloader, installer and journal, wired against a loopback
    /// <see cref="TestFileServer"/> serving <paramref name="archive"/>. Only the delay provider is
    /// faked, so a retry cannot sleep for real time; nothing here provokes one.
    /// </summary>
    private static Rig RealQueue(
        TempDir temp, HttpClient client, TestFileServer server, byte[] archive,
        long chunkSize, int connections, string[]? installDirs = null,
        Action<InstallProgress>? entryWritten = null)
    {
        var paths = new DownloadPaths(Path.Combine(temp.Path, "downloads"));
        var appPaths = new AppPaths(Path.Combine(temp.Path, "config"));
        var state = new InstallStateStore(appPaths.InstallStateDirectory);
        var gameDir = Path.Combine(temp.Path, "The Sims 4");
        Directory.CreateDirectory(gameDir);

        var workflow = new PackWorkflow(
            new SegmentedDownloader(
                client, paths,
                new DownloadOptions { Connections = connections, ChunkSize = chunkSize },
                RetryOptions.Default, new FakeDelayProvider()),
            new ZipInstaller(state, entryWritten: entryWritten),
            paths);

        var pack = new PackEntry(
            "EP01", "The Sims 4 Get to Work", PackType.Expansion, archive.LongLength, null,
            Sha256Of(archive), new[] { server.FileUrl }, installDirs ?? new[] { "EP01" });

        // ProgressInterval zero: the controls below fire off progress, so rate-limiting the
        // publishes would rate-limit the test's own trigger.
        var queue = new PackQueue(workflow, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });

        return new Rig(queue, pack, gameDir, paths, state);
    }

    /// <summary>Adjacent duplicates collapsed, so a state sequence reads as transitions.</summary>
    private static void Record(List<QueueItemState> states, QueueItemState state)
    {
        if (states.Count == 0 || states[^1] != state) states.Add(state);
    }

    private static string DirectoriesIn(string gameDirectory) =>
        string.Join(", ", Directory.EnumerateDirectories(gameDirectory).Select(Path.GetFileName));

    [Fact(Timeout = 30000)]
    public async Task A_real_pack_downloads_installs_and_scans_as_installed()
    {
        const int connections = 4;

        var archive = TestArchive(
            ("EP01/ClientFullBuild0.package", 60_000), ("EP01/Strings.package", 40_000));
        await using var server = await TestFileServer.StartAsync(archive);
        using var temp = new TempDir();
        using var client = HttpFactory.Create(connections * 2);

        var rig = RealQueue(temp, client, server, archive, chunkSize: 20_000, connections: connections);
        await using var queue = rig.Queue;
        var run = queue.RunAsync(CancellationToken.None);

        queue.Enqueue(rig.Pack, rig.GameDir);
        queue.Complete();
        await run;

        // Drained after the run rather than by a concurrent reader: the channel is closed but
        // its backlog is still readable, and nothing here needs to act mid-run.
        var states = new List<QueueItemState>();
        await foreach (var update in queue.Updates)
            foreach (var item in update.Items)
                Record(states, item.State);

        // The phases really ran, rather than the pack being short-circuited by an archive that
        // happened to be on disk. Verifying is skipped by design when there is no archive.
        Assert.Contains(QueueItemState.Downloading, states);
        Assert.Contains(QueueItemState.Installing, states);
        Assert.Equal(QueueItemState.Completed, states[^1]);

        // Existence alone passes against an installer that creates every file and copies nothing.
        // The archive is stored rather than deflated, so each extracted length is exactly the
        // size asked for.
        Assert.Equal(60_000, new FileInfo(Path.Combine(rig.GameDir, "EP01", "ClientFullBuild0.package")).Length);
        Assert.Equal(40_000, new FileInfo(Path.Combine(rig.GameDir, "EP01", "Strings.package")).Length);

        // The engine cleans up after itself: the installer deletes the archive it consumed and
        // the finalizer removes the partial file and its sidecar.
        Assert.False(File.Exists(rig.Paths.ArchiveFile("EP01")));
        Assert.False(File.Exists(rig.Paths.PartFile("EP01")));
        Assert.False(File.Exists(rig.Paths.StateFile("EP01")));

        var marker = rig.State.TryLoad(rig.GameDir, "EP01");
        Assert.NotNull(marker);
        Assert.Equal(InstallMarkerStatus.Installed, marker!.Status);
        Assert.Equal(rig.Pack.Sha256, marker.ArchiveSha256);

        var scan = InstallScanner.Scan(rig.GameDir, new[] { rig.Pack }, rig.State.LoadAll(rig.GameDir));
        Assert.Equal(PackInstallState.Installed, scan.Packs[0].State);
    }

    [Fact(Timeout = 30000)]
    public async Task Pause_mid_download_keeps_the_part_and_the_resume_refetches_nothing_already_done()
    {
        const long chunkSize = 100_000;
        const int connections = 4;

        var archive = TestArchive(("EP01/ClientFullBuild0.package", 3_000_000));
        var plan = ChunkPlan.Create(archive.LongLength, chunkSize);
        var chunkCount = plan.Count;

        await using var server = await TestFileServer.StartAsync(archive);
        using var temp = new TempDir();
        using var client = HttpFactory.Create(connections * 2);

        var rig = RealQueue(temp, client, server, archive, chunkSize, connections);
        await using var queue = rig.Queue;
        var run = queue.RunAsync(CancellationToken.None);

        var reachedPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<QueueItemState>();
        QueueItemSnapshot? atPause = null;
        var paused = false;

        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
            {
                var item = update.Items.FirstOrDefault();
                if (item is null) continue;

                Record(states, item.State);

                // BytesCompleted is committed plus provisional bytes (ProgressTracker.Snapshot),
                // and a worker's provisional balance is at most one chunk, cleared by CommitChunk
                // and Abandon. So with N workers, BytesCompleted above N chunks means at least one
                // chunk is committed, and a chunk is committed only after PartStateStore.SaveAsync
                // returns. Pausing on the first byte would routinely land before any chunk was
                // recorded. If that accounting ever changes, `alreadyDone > 0` below fails with its
                // own message rather than degrading into a timing race.
                if (!paused && item.State == QueueItemState.Downloading
                    && item.BytesCompleted > connections * chunkSize)
                {
                    paused = true;
                    queue.Pause();
                }

                // The pause has landed only when RunItemAsync's finally has published Paused. By
                // then the runner has unwound and Settle has turned the cancellation back into a
                // Queued item.
                if (paused && update.State == QueueState.Paused)
                {
                    atPause = item;
                    reachedPaused.TrySetResult();
                }
            }
        });

        queue.Enqueue(rig.Pack, rig.GameDir);
        await reachedPaused.Task;

        // A paused pack is queued again, waiting for a Resume. The message also reports a pause
        // that landed after the item had already finished.
        Assert.True(atPause!.State == QueueItemState.Queued,
            $"expected the paused pack to be back in Queued; got {atPause.State}. "
            + $"states seen {string.Join(" -> ", states)}. Completed would mean the download "
            + "finished before the pause landed and there is nothing left to resume; "
            + "Failed would mean the pause was reported as an error.");

        // Both engine components return result-shaped cancellations rather than throwing past the
        // queue, so InvokeRunnerAsync's OperationCanceledException catch is never reached and the
        // framework's "The operation was canceled." never lands in the user's error column.
        Assert.Null(atPause.Error);

        // "The .part exists" is no use as a wait predicate: SegmentedDownloader preallocates it
        // before any worker starts.
        var partFile = rig.Paths.PartFile("EP01");
        Assert.True(File.Exists(partFile),
            "the partial file is gone, so the download finished before the pause landed and there is "
            + "nothing to resume; lengthen the archive");
        Assert.Equal(archive.LongLength, new FileInfo(partFile).Length);

        var sidecar = new PartStateStore(rig.Paths.StateFile("EP01")).TryLoad();
        Assert.NotNull(sidecar);

        var done = sidecar!.CompletedChunks.Select(c => c.Index).ToHashSet();
        var alreadyDone = done.Count;
        Assert.True(alreadyDone > 0,
            "the pause landed before any chunk completed, so the assertion below would hold even if "
            + "the resume refetched the whole archive; lengthen the archive");
        Assert.True(alreadyDone < chunkCount,
            $"all {chunkCount} chunks were already recorded, so the resume had nothing to fetch and "
            + "the bound below proves nothing; shorten the pause trigger or lengthen the archive");

        server.ResetCounters();
        queue.Resume();
        queue.Complete();
        await run;
        await reader;

        Assert.Equal(QueueItemState.Completed, states[^1]);

        // Exactly the chunks the sidecar did not already vouch for, by index rather than by a
        // count of requests. A request already on the wire when the pause landed can still reach
        // the server after ResetCounters, which is one more than a count expects; the chunk it
        // names was in flight and so is not one the sidecar vouched for, which puts it in the set
        // below already. A set is unchanged by it, and by a repeat. Equality both ways, so this
        // still catches an under-fetching resume that a ceiling would leave to the SHA-256 gate.
        //
        // The probe is bytes=0-0 (RangeProbe), which no chunk range matches.
        var asked = server.ReceivedRangeHeaders
            .Where(h => h is not null && h != "bytes=0-0")
            .Select(h => plan.Single(c => h == $"bytes={c.Start}-{c.EndInclusive}").Index)
            .ToHashSet();

        Assert.Equal(plan.Select(c => c.Index).Where(i => !done.Contains(i)).ToHashSet(), asked);

        // A ceiling as well, because a set is unchanged by a chunk fetched twice: set equality
        // alone would not see a resume that dispatched every outstanding chunk to two workers. The
        // slack is named rather than guessed — one range probe, plus at most `connections` requests
        // that were on the wire when the pause landed and reach the server after ResetCounters.
        Assert.True(server.RequestCount <= asked.Count + 1 + connections,
            $"the resume made {server.RequestCount} requests for {asked.Count} chunks, more than "
            + $"one probe and {connections} in-flight stragglers can account for");

        // The archive is stored rather than deflated, so the extracted length is exactly what went
        // in, and checking it needs neither a retained archive nor a second hash.
        Assert.Equal(3_000_000, new FileInfo(
            Path.Combine(rig.GameDir, "EP01", "ClientFullBuild0.package")).Length);
    }

    [Fact(Timeout = 30000)]
    public async Task Cancel_mid_install_leaves_the_scanner_reporting_partial()
    {
        const int connections = 4;

        // Two install directories so the scan can distinguish Partial from NotInstalled, and the
        // Delta entry last so cancelling during the EP01 entries leaves Delta absent.
        var archive = TestArchive(
            ("EP01/a.package", 200_000),
            ("EP01/b.package", 200_000),
            ("Delta/EP01/c.bin", 200_000));

        await using var server = await TestFileServer.StartAsync(archive);
        using var temp = new TempDir();
        using var client = HttpFactory.Create(connections * 2);

        var entries = new List<string>();
        PackQueue? running = null;

        // The cancel is made from the extracting thread as an entry lands, which is where
        // ZipInstaller offers it, so it is ordered before the next entry's token check rather
        // than racing the write. An entry under EP01/ has landed by then, so EP01 exists and the
        // cancel is inside the window rather than before the install touched anything.
        var rig = RealQueue(
            temp, client, server, archive, chunkSize: 1_000_000, connections: connections,
            installDirs: new[] { "EP01", "Delta" },
            entryWritten: landed =>
            {
                if (!landed.CurrentEntry.StartsWith("EP01/", StringComparison.Ordinal)) return;

                // The diagnostic a wrong-window run is reported with, in write order.
                entries.Add(landed.CurrentEntry);
                if (entries.Count == 1) running!.Cancel("EP01");
            });

        await using var queue = rig.Queue;
        running = queue;

        var run = queue.RunAsync(CancellationToken.None);

        var states = new List<QueueItemState>();
        QueueItemSnapshot? last = null;

        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
            {
                var item = update.Items.FirstOrDefault();
                if (item is null) continue;

                last = item;
                Record(states, item.State);
            }
        });

        queue.Enqueue(rig.Pack, rig.GameDir);
        queue.Complete();
        await run;
        await reader;

        Assert.NotEmpty(entries);

        var marker = rig.State.TryLoad(rig.GameDir, "EP01");
        var scan = InstallScanner.Scan(rig.GameDir, new[] { rig.Pack }, rig.State.LoadAll(rig.GameDir));
        var context =
            $"item ended {states[^1]}, entries written {string.Join(", ", entries)}, "
            + $"directories present [{DirectoriesIn(rig.GameDir)}], "
            + $"marker {marker?.Status.ToString() ?? "absent"}. "
            + "NotInstalled would mean the cancel landed before the install created a directory; "
            + "Installed would mean it landed after the extract had finished.";

        // Both the queue's verdict and the scanner's: a scan reads Partial off an install that
        // merely failed, and a Cancelled item says nothing about what the next scan reports.
        Assert.True(states[^1] == QueueItemState.Cancelled, $"expected the pack to end Cancelled; {context}");
        Assert.True(scan.Packs[0].State == PackInstallState.Partial,
            $"expected the scan to read Partial; got {scan.Packs[0].State}. {context}");

        // Partial by the subset rule: EP01 was written, Delta never was.
        Assert.Equal(new[] { "Delta" }, scan.Packs[0].MissingDirs);

        // And the journal independently records the interruption, which is what would keep the
        // pack out of "installed" even if every directory had happened to exist.
        Assert.NotNull(marker);
        Assert.Equal(InstallMarkerStatus.Installing, marker!.Status);

        // A cancel is not an error either: ZipInstaller returns InstallResult.Cancelled rather
        // than letting OperationCanceledException reach the queue, so the framework's
        // "The operation was canceled." never becomes the user's error text. See the matching
        // assertion in the pause test.
        Assert.NotNull(last);
        Assert.Null(last!.Error);
    }

    /// <summary>
    /// <see cref="InstallScanner"/>'s other Partial branch: an empty <c>MissingDirs</c>, meaning
    /// every install directory is present and only the journal says the extraction never finished.
    /// The test above stops at the subset branch and never consults the marker.
    ///
    /// Same window as that test, arranged differently. <c>Delta/x.bin</c> is written first so both
    /// directories exist before the cancel, the trigger fires on <c>a.package</c>, and the deadline
    /// is the token check before <c>tail.package</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Cancel_mid_install_with_every_directory_present_reports_partial_from_the_journal()
    {
        const int connections = 4;

        var archive = TestArchive(
            ("Delta/x.bin", 200_000),
            ("EP01/a.package", 200_000),
            ("EP01/tail.package", 200_000));

        await using var server = await TestFileServer.StartAsync(archive);
        using var temp = new TempDir();
        using var client = HttpFactory.Create(connections * 2);

        var entries = new List<string>();
        PackQueue? running = null;

        // Delta/x.bin precedes every EP01 entry in the archive, so an entry landing under EP01/
        // means both install directories are already on disk, which sends the scan down the
        // journal branch rather than the subset branch.
        var rig = RealQueue(
            temp, client, server, archive, chunkSize: 1_000_000, connections: connections,
            installDirs: new[] { "EP01", "Delta" },
            entryWritten: landed =>
            {
                if (!landed.CurrentEntry.StartsWith("EP01/", StringComparison.Ordinal)) return;

                entries.Add(landed.CurrentEntry);
                if (entries.Count == 1) running!.Cancel("EP01");
            });

        await using var queue = rig.Queue;
        running = queue;

        var run = queue.RunAsync(CancellationToken.None);

        var states = new List<QueueItemState>();
        QueueItemSnapshot? last = null;

        var reader = Task.Run(async () =>
        {
            await foreach (var update in queue.Updates)
            {
                var item = update.Items.FirstOrDefault();
                if (item is null) continue;

                last = item;
                Record(states, item.State);
            }
        });

        queue.Enqueue(rig.Pack, rig.GameDir);
        queue.Complete();
        await run;
        await reader;

        Assert.NotEmpty(entries);

        var marker = rig.State.TryLoad(rig.GameDir, "EP01");
        var scan = InstallScanner.Scan(rig.GameDir, new[] { rig.Pack }, rig.State.LoadAll(rig.GameDir));
        var tail = Path.Combine(rig.GameDir, "EP01", "tail.package");
        var context =
            $"item ended {states[^1]}, entries written {string.Join(", ", entries)}, "
            + $"directories present [{DirectoriesIn(rig.GameDir)}], "
            + $"marker {marker?.Status.ToString() ?? "absent"}, "
            + $"tail.package {(File.Exists(tail) ? "written" : "absent")}. "
            + "A written tail.package means the cancel missed its deadline (the token check "
            + "before the last entry) and the extract ran to completion.";

        Assert.True(states[^1] == QueueItemState.Cancelled, $"expected the pack to end Cancelled; {context}");
        Assert.False(File.Exists(tail), $"the last entry was written after all; {context}");

        Assert.True(scan.Packs[0].State == PackInstallState.Partial,
            $"expected the scan to read Partial; got {scan.Packs[0].State}. {context}");

        // Every install directory is present, so InstallScanner reached its journal branch and
        // read an open extraction rather than applying the subset rule.
        Assert.Empty(scan.Packs[0].MissingDirs);

        Assert.NotNull(marker);
        Assert.Equal(InstallMarkerStatus.Installing, marker!.Status);

        Assert.NotNull(last);
        Assert.Null(last!.Error);
    }
}
