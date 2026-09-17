using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;
using LamSims.Core.Logging;
using static LamSims.Core.Tests.Payloads;

namespace LamSims.Core.Tests;

public class DownloadLoggingTests
{
    /// <summary>
    /// A fixed chunk size so a 200,000-byte payload always splits into exactly four chunks,
    /// which is what lets the connections-count test assert an exact number.
    /// </summary>
    private const long ChunkSize = 50_000;

    /// <summary>
    /// The TestFileServer parameter is unused: every test builds its DownloadRequest's mirror
    /// list directly from the servers it starts, so Build only needs a place to root the
    /// download and a client. Kept in the signature so every call site names the primary server
    /// it is exercising, matching the shape SegmentedDownloaderTests already uses.
    /// </summary>
    private static SegmentedDownloader Build(TempDir dir, TestFileServer server, ILogSink log, int connections)
    {
        var paths = new DownloadPaths(dir.Path);
        var client = HttpFactory.Create(8);
        return new SegmentedDownloader(
            client, paths, new DownloadOptions { Connections = connections, ChunkSize = ChunkSize },
            RetryOptions.Default, new FakeDelayProvider(), log);
    }

    [Fact]
    public async Task A_download_logs_the_url_the_size_and_the_connection_count()
    {
        var payload = Payloads.Random(200_000);
        using var dir = new TempDir();
        await using var server = await TestFileServer.StartAsync(payload);
        var log = new RecordingLogSink();
        var downloader = Build(dir, server, log, connections: 4);

        var result = await downloader.DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, payload.LongLength, Sha256Of(payload)),
            null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(log.Logged(server.FileUrl.ToString()),
                    $"no line named the URL: {string.Join(" | ", log.Texts)}");
        Assert.True(log.Logged("over 4 connections"));
        Assert.True(log.Logged("sha256 verified"));
    }

    /// <summary>
    /// 200,000 bytes over a 50,000-byte chunk size plans exactly 4 chunks; capping connections at
    /// 3 leaves the real worker count (min(connections, pending chunks) = 3) strictly below the
    /// chunk count. The test above alone cannot catch a line that prints chunks.Count instead of
    /// the worker count, because it used connections: 4 — the same number as the chunk plan — so
    /// the two values agreed by coincidence and either one would have passed it. This is the
    /// discriminating case: it fails against a line reading "over 4 connections" and only passes
    /// against the true worker count, "over 3 connections".
    /// </summary>
    [Fact]
    public async Task A_download_logs_the_real_worker_count_not_the_chunk_count_when_they_differ()
    {
        var payload = Payloads.Random(200_000);
        using var dir = new TempDir();
        await using var server = await TestFileServer.StartAsync(payload);
        var log = new RecordingLogSink();
        var downloader = Build(dir, server, log, connections: 3);

        var result = await downloader.DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, payload.LongLength, Sha256Of(payload)),
            null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(log.Logged("over 3 connections"),
                    $"expected the real worker count (3), not the chunk count (4): {string.Join(" | ", log.Texts)}");
        Assert.False(log.Logged("over 4 connections"),
                    $"logged the chunk count instead of the worker count: {string.Join(" | ", log.Texts)}");
    }

    /// <summary>
    /// When no mirror honours ranges, SegmentedDownloader returns into SingleStreamDownloader
    /// before ever building a chunk plan or a worker pool — and SingleStreamDownloader itself logs
    /// no start line at all (it only logs the terminal sha256/digest lines). Without a start line
    /// on this path, a user on a range-less mirror sees nothing between the queue starting and the
    /// digest line: minutes of apparently dead log on a big pack.
    /// </summary>
    [Fact]
    public async Task A_range_less_mirror_still_logs_the_url_and_size_with_no_connection_count()
    {
        var payload = Payloads.Random(200_000);
        using var dir = new TempDir();
        await using var server = await TestFileServer.StartAsync(
            payload, new TestFileServerOptions { SupportRanges = false });
        var log = new RecordingLogSink();
        var downloader = Build(dir, server, log, connections: 4);

        var result = await downloader.DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, payload.LongLength, Sha256Of(payload)),
            null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(result.UsedSingleStream);
        Assert.True(log.Logged(server.FileUrl.ToString()),
                    $"no line named the URL: {string.Join(" | ", log.Texts)}");
        Assert.True(log.Logged(LogFormat.Bytes(payload.LongLength)),
                    $"no line named the size: {string.Join(" | ", log.Texts)}");
        Assert.False(log.Logged("connections"),
                    $"a connection count was logged on a path that never runs workers: {string.Join(" | ", log.Texts)}");
    }

    /// <summary>
    /// The line no other test in the suite produces. A mirror that dies mid-transfer is the case
    /// a user most needs explained, and without this line the log shows a stall and no reason.
    /// </summary>
    [Fact]
    public async Task A_rotation_to_the_next_mirror_says_which_mirror_failed_and_why()
    {
        var payload = Payloads.Random(200_000);
        using var dir = new TempDir();
        // FailNextRequests, not a StatusCode option — that is the idiom this suite already uses
        // for a mirror that answers 500 (RangeProbeTests.cs:115, TestFileServerTests.cs:103).
        await using var dead = await TestFileServer.StartAsync(payload, new TestFileServerOptions
        {
            FailNextRequests = int.MaxValue,
        });
        await using var good = await TestFileServer.StartAsync(payload);
        var log = new RecordingLogSink();
        var downloader = Build(dir, good, log, connections: 1);

        // DownloadRequest is (string Code, IReadOnlyList<Uri> Urls, long Size, string Sha256)
        // and TestFileServer.FileUrl is already a Uri — build it directly, as every existing
        // SegmentedDownloaderTests call site does.
        var request = new DownloadRequest(
            "EP01", new[] { dead.FileUrl, good.FileUrl }, payload.LongLength, Sha256Of(payload));
        var result = await downloader.DownloadAsync(request, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(log.Logged(dead.FileUrl.ToString(), LogSeverity.Warning),
                    $"the dead mirror was not named as a warning: {string.Join(" | ", log.Texts)}");
        Assert.True(log.Logged("rotating"));
    }

    /// <summary>
    /// The line above (A_rotation_to_the_next_mirror...) kills the mirror's probe, so the dead
    /// mirror never reaches ChunkFetcher at all: SegmentedDownloader.ProbeMirrorsAsync excludes
    /// it before any chunk is ever fetched. This test instead lets both mirrors probe
    /// successfully, and only fails the bad one on the larger chunk-fetch request (DropAfterBytes
    /// is smaller than the probe's 1-byte range response but well inside a 50,000-byte chunk), so
    /// it is ChunkFetcher.FetchAsync itself that rotates — the line this test covers.
    /// </summary>
    [Fact]
    public async Task A_chunk_fetch_that_fails_once_rotates_to_the_next_mirror_and_logs_why()
    {
        var payload = Payloads.Random(200_000);
        using var dir = new TempDir();
        await using var bad = await TestFileServer.StartAsync(payload, new TestFileServerOptions
        {
            // Below the probe's 1-byte range response, so probing succeeds; above it and below
            // the 50,000-byte chunk size, so every real chunk fetch is aborted mid-transfer.
            // DropAfterBytesCount defaults to int.MaxValue, so the mirror never recovers.
            DropAfterBytes = 1024,
        });
        await using var good = await TestFileServer.StartAsync(payload);
        var log = new RecordingLogSink();
        var downloader = Build(dir, good, log, connections: 1);

        var request = new DownloadRequest(
            "EP01", new[] { bad.FileUrl, good.FileUrl }, payload.LongLength, Sha256Of(payload));
        var result = await downloader.DownloadAsync(request, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(log.Logged(bad.FileUrl.ToString(), LogSeverity.Warning),
                    $"the bad mirror was not named as a warning: {string.Join(" | ", log.Texts)}");
        Assert.True(log.Logged("rotating"));
    }

    /// <summary>Same fixture as above; this one covers the retry line rather than the warning.</summary>
    [Fact]
    public async Task A_chunk_fetch_that_fails_once_logs_a_retry_before_the_next_attempt()
    {
        var payload = Payloads.Random(200_000);
        using var dir = new TempDir();
        await using var bad = await TestFileServer.StartAsync(payload, new TestFileServerOptions
        {
            DropAfterBytes = 1024,
        });
        await using var good = await TestFileServer.StartAsync(payload);
        var log = new RecordingLogSink();
        var downloader = Build(dir, good, log, connections: 1);

        var request = new DownloadRequest(
            "EP01", new[] { bad.FileUrl, good.FileUrl }, payload.LongLength, Sha256Of(payload));
        var result = await downloader.DownloadAsync(request, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        // RetryOptions.Default.MaxAttempts is 5; the bad mirror always fails on the first
        // attempt for every chunk, so "Retry 1 of 5" is logged at least once, at Info severity.
        Assert.True(log.Logged("Retry 1 of 5 for chunk", LogSeverity.Info),
                    $"no retry line was logged: {string.Join(" | ", log.Texts)}");
    }

    [Fact]
    public async Task A_digest_mismatch_is_logged_as_an_error_naming_both_digests()
    {
        var payload = Payloads.Random(200_000);
        using var dir = new TempDir();
        await using var server = await TestFileServer.StartAsync(payload);
        var log = new RecordingLogSink();
        var downloader = Build(dir, server, log, connections: 1);

        var request = new DownloadRequest(
            "EP01", new[] { server.FileUrl }, payload.LongLength, new string('a', 64));
        var result = await downloader.DownloadAsync(request, null, CancellationToken.None);

        Assert.NotEqual(DownloadOutcome.Completed, result.Outcome);
        Assert.True(log.Logged("Digest mismatch", LogSeverity.Error));
    }
}
