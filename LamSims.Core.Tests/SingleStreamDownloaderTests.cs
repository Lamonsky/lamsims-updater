using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Security.Cryptography;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class SingleStreamDownloaderTests
{
    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Downloads_and_verifies_without_ranges()
    {
        var content = Payload(200_000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(4);

        var result = await new SingleStreamDownloader(client, paths, RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                new[] { server.FileUrl }, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(result.UsedSingleStream);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task Restarts_from_zero_on_retry_because_it_cannot_resume()
    {
        var content = Payload(100_000);
        // Exactly the first response is truncated; the retry is served in full.
        var options = new TestFileServerOptions
        {
            SupportRanges = false, DropAfterBytes = 1000, DropAfterBytesCount = 1,
        };
        await using var server = await TestFileServer.StartAsync(content, options);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(4);

        var result = await new SingleStreamDownloader(client, paths, RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                new[] { server.FileUrl }, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
        // The whole transfer restarted: no resume is possible without ranges.
        Assert.Equal(2, server.RequestCount);
    }

    [Fact(Timeout = 15000)]
    public async Task A_stalled_transfer_fails_rather_than_hanging()
    {
        // Same stall-detection contract as ChunkFetcher, on the no-ranges path. The
        // [Fact(Timeout=…)] guard bounds the test itself.
        var content = Payload(100_000);
        var options = new TestFileServerOptions
        {
            SupportRanges = false,
            StallAfterBytes = 10,
            StallFor = TimeSpan.FromSeconds(5),
        };
        await using var server = await TestFileServer.StartAsync(content, options);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(4);
        var shortTimeout = RetryOptions.Default with { ReadTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await new SingleStreamDownloader(client, paths, shortTimeout, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                new[] { server.FileUrl }, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("sent no data", result.Error!);
    }

    [Fact(Timeout = 15000)]
    public async Task A_server_that_never_answers_fails_rather_than_hanging()
    {
        var content = Payload(100_000);
        var options = new TestFileServerOptions
        {
            SupportRanges = false,
            StallBeforeHeaders = TimeSpan.FromSeconds(5),
        };
        await using var server = await TestFileServer.StartAsync(content, options);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(4);
        var shortTimeout = RetryOptions.Default with { HeaderTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await new SingleStreamDownloader(client, paths, shortTimeout, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                new[] { server.FileUrl }, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("sent no response headers", result.Error!);
    }

    [Fact]
    public async Task A_stream_that_ends_early_is_retried_rather_than_quarantined()
    {
        // A short stream ends cleanly, so the read loop sees a normal EOF. Reaching the checksum
        // with it would quarantine the archive, which the user must then clear by hand; a
        // truncated transfer belongs in the retry loop.
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(4);

        // The catalog says 2000 bytes; the server has 1000 and ends without error.
        var result = await new SingleStreamDownloader(client, paths, RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, 2000, Sha256Of(content)),
                new[] { server.FileUrl }, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("ended after 1000 of 2000 bytes", result.Error!);
        Assert.False(File.Exists(paths.QuarantineFile("EP01")));
        Assert.Equal(RetryOptions.Default.MaxAttempts, server.RequestCount);
    }

    [Fact]
    public async Task Refuses_to_write_past_the_expected_size()
    {
        // Nothing bounds a response body by itself, so a server that keeps sending would fill
        // the disk. One buffer is the most it can overshoot before the write is refused.
        var content = Payload(300_000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(4);

        var result = await new SingleStreamDownloader(client, paths, RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, 1000, Sha256Of(content)),
                new[] { server.FileUrl }, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("more than the expected 1000 bytes", result.Error!);
        Assert.False(File.Exists(paths.QuarantineFile("EP01")));
    }

    [Fact]
    public async Task Cancellation_reports_the_single_stream_mode()
    {
        var content = Payload(400_000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(4);
        using var cts = new CancellationTokenSource();

        var progress = new SyncProgress<DownloadProgress>(_ => cts.Cancel());

        var result = await new SingleStreamDownloader(client, paths, RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                new[] { server.FileUrl }, progress, cts.Token);

        Assert.Equal(DownloadOutcome.Cancelled, result.Outcome);
        Assert.True(result.UsedSingleStream);
    }

    [PosixDenialFact]
    public async Task An_unwritable_download_root_is_reported_as_a_failure()
    {
        // The root cannot even be created, so EnsureCreated itself throws. It must sit inside
        // the try: this method always returns a DownloadResult, whatever the filesystem does.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var temp = new TempDir();
        using var locked = new ReadOnlyDir(temp.Path);
        using var client = HttpFactory.Create(4);

        var result = await new SingleStreamDownloader(
                client, new DownloadPaths(locked.Child), RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                new[] { server.FileUrl }, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task The_segmented_downloader_falls_back_when_no_mirror_honours_ranges()
    {
        var content = Payload(150_000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(4);

        var result = await new SegmentedDownloader(
                client, paths, new DownloadOptions { Connections = 4, ChunkSize = 16 * 1024 },
                RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(result.UsedSingleStream);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task The_fallback_rotates_across_every_rangeless_mirror()
    {
        // The fallback cannot resume, so a mirror that dies mid-transfer is a total loss and
        // re-requesting it on every attempt never reaches the healthy alternate.
        var content = Payload(150_000);
        await using var broken = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false, DropAfterBytes = 10 });
        await using var healthy = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(4);

        var result = await new SegmentedDownloader(
                client, paths, new DownloadOptions { Connections = 4, ChunkSize = 16 * 1024 },
                RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { broken.FileUrl, healthy.FileUrl },
                    content.LongLength, Sha256Of(content)),
                progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(result.UsedSingleStream);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
        Assert.True(healthy.RequestCount > 1, "the second rangeless mirror was never asked for the body");
    }

    [Fact]
    public async Task A_ranged_mirror_is_preferred_over_one_without_ranges()
    {
        var content = Payload(150_000);
        await using var noRanges = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        await using var ranges = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(4);

        var result = await new SegmentedDownloader(
                client, paths, new DownloadOptions { Connections = 4, ChunkSize = 16 * 1024 },
                RetryOptions.Default, new FakeDelayProvider())
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { noRanges.FileUrl, ranges.FileUrl },
                    content.LongLength, Sha256Of(content)),
                progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.False(result.UsedSingleStream);
    }
}
