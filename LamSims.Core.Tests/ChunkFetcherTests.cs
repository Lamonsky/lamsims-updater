using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class ChunkFetcherTests
{
    private static byte[] Payload(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    private static MirrorSource Mirror(Uri url, string? etag = "\"v1\"") =>
        new(url, new MirrorValidator(url.ToString(), etag, null, null));

    private static (string Path, SafeFileHandleWrapper Handle) PreparedTarget(TempDir temp, long size)
    {
        var path = temp.File("EP01.part");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) fs.SetLength(size);
        return (path, new SafeFileHandleWrapper(File.OpenHandle(
            path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous)));
    }

    /// <summary>Keeps the handle disposable inside a using statement.</summary>
    public sealed class SafeFileHandleWrapper(Microsoft.Win32.SafeHandles.SafeFileHandle handle) : IDisposable
    {
        public Microsoft.Win32.SafeHandles.SafeFileHandle Handle { get; } = handle;
        public void Dispose() => Handle.Dispose();
    }

    /// <summary>Records every call verbatim, including which worker made it, for assertion.</summary>
    private sealed class RecordingProgress : IChunkProgress
    {
        public List<(int Worker, long Bytes)> Advances { get; } = new();
        public List<int> Abandons { get; } = new();

        public void Advanced(int worker, long bytes) => Advances.Add((worker, bytes));
        public void Abandoned(int worker) => Abandons.Add(worker);
    }

    [Fact]
    public async Task Writes_the_chunk_at_its_offset()
    {
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var (path, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        using (target)
            await fetcher.FetchAsync(new Chunk(1, 100, 100), new[] { Mirror(server.FileUrl) }, 0, 0,
                target.Handle, null, CancellationToken.None);

        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(content[100..200], written[100..200]);
    }

    [Fact]
    public async Task Sends_the_range_and_if_range_headers()
    {
        await using var server = await TestFileServer.StartAsync(Payload(1000));
        using var temp = new TempDir();
        var (_, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        using (target)
            await fetcher.FetchAsync(new Chunk(0, 0, 50), new[] { Mirror(server.FileUrl) }, 0, 0,
                target.Handle, null, CancellationToken.None);

        Assert.Equal(new[] { "bytes=0-49" }, server.ReceivedRangeHeaders);
        // If-Range is what makes the request conditional: without it a file changing on the
        // server mid-download would go undetected until the final checksum.
        Assert.Equal(new[] { "\"v1\"" }, server.ReceivedIfRangeHeaders);
    }

    [Fact]
    public async Task Omits_if_range_for_a_mirror_with_no_usable_validator()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(1000), new TestFileServerOptions { ETag = null });
        using var temp = new TempDir();
        var (_, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        using (target)
            await fetcher.FetchAsync(new Chunk(0, 0, 50), new[] { Mirror(server.FileUrl, etag: null) }, 0, 0,
                target.Handle, null, CancellationToken.None);

        Assert.Equal(new string?[] { null }, server.ReceivedIfRangeHeaders);
    }

    [Fact]
    public async Task Retries_after_a_503_and_backs_off_without_sleeping()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(1000), new TestFileServerOptions { FailNextRequests = 2 });
        using var temp = new TempDir();
        var (path, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var delays = new FakeDelayProvider();
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, delays);

        using (target)
            await fetcher.FetchAsync(new Chunk(0, 0, 100), new[] { Mirror(server.FileUrl) }, 0, 0,
                target.Handle, null, CancellationToken.None);

        Assert.Equal(2, delays.Delays.Count);
        Assert.True(delays.Delays[1] > delays.Delays[0], "backoff should grow");
        Assert.Equal(Payload(1000)[0..100], (await File.ReadAllBytesAsync(path))[0..100]);
    }

    [Fact]
    public async Task Rotates_to_the_next_mirror_when_one_fails()
    {
        await using var bad = await TestFileServer.StartAsync(
            Payload(1000), new TestFileServerOptions { FailNextRequests = 100 });
        await using var good = await TestFileServer.StartAsync(Payload(1000));
        using var temp = new TempDir();
        var (path, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        MirrorSource served;
        using (target)
            served = await fetcher.FetchAsync(new Chunk(0, 0, 100),
                new[] { Mirror(bad.FileUrl), Mirror(good.FileUrl) }, 0, 0, target.Handle, null, CancellationToken.None);

        Assert.Equal(good.FileUrl, served.Url);
        Assert.True(good.RequestCount >= 1);
        Assert.Equal(Payload(1000)[0..100], (await File.ReadAllBytesAsync(path))[0..100]);
    }

    [Fact]
    public async Task Recovers_from_a_connection_dropped_mid_body()
    {
        var content = Payload(100_000);
        // Exactly the first response is truncated; the retry is served in full.
        var options = new TestFileServerOptions { DropAfterBytes = 10, DropAfterBytesCount = 1 };
        await using var server = await TestFileServer.StartAsync(content, options);
        using var temp = new TempDir();
        var (path, target) = PreparedTarget(temp, 100_000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        using (target)
            await fetcher.FetchAsync(new Chunk(0, 0, 50_000), new[] { Mirror(server.FileUrl) }, 0, 0,
                target.Handle, null, CancellationToken.None);

        Assert.Equal(2, server.RequestCount);
        Assert.Equal(content[0..50_000], (await File.ReadAllBytesAsync(path))[0..50_000]);
    }

    [Fact]
    public async Task A_200_response_to_a_range_request_means_the_entity_changed()
    {
        // If-Range: a matching validator yields 206, a stale one yields 200 with the whole entity.
        await using var server = await TestFileServer.StartAsync(
            Payload(1000), new TestFileServerOptions { ETag = "\"v2\"" });
        using var temp = new TempDir();
        var (_, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        using (target)
        {
            var ex = await Assert.ThrowsAsync<ValidatorMismatchException>(
                () => fetcher.FetchAsync(new Chunk(0, 0, 100),
                    new[] { Mirror(server.FileUrl, "\"stale\"") }, 0, 0, target.Handle, null, CancellationToken.None));

            Assert.Equal(server.FileUrl.ToString(), ex.Url);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task A_stalled_response_fails_into_retry_instead_of_hanging()
    {
        // The server sends a few bytes, then stalls well past the fetcher's read timeout.
        // The [Fact(Timeout=…)] guard bounds the test itself, so a missing per-read deadline
        // fails the suite instead of leaving CI stuck.
        var options = new TestFileServerOptions
        {
            StallAfterBytes = 10,
            StallFor = TimeSpan.FromSeconds(5),
        };
        await using var server = await TestFileServer.StartAsync(Payload(1000), options);
        using var temp = new TempDir();
        var (_, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var shortTimeout = RetryOptions.Default with { ReadTimeout = TimeSpan.FromMilliseconds(200) };
        var fetcher = new ChunkFetcher(client, shortTimeout, new FakeDelayProvider());

        using (target)
        {
            var ex = await Assert.ThrowsAsync<IOException>(
                () => fetcher.FetchAsync(new Chunk(0, 0, 100), new[] { Mirror(server.FileUrl) }, 0,
                    0, target.Handle, null, CancellationToken.None));

            Assert.Contains("sent no data", ex.Message);
        }

        // Every attempt hit the stall and had to be retried; nothing ever succeeded.
        Assert.Equal(RetryOptions.Default.MaxAttempts, server.RequestCount);
    }

    [Fact(Timeout = 15000)]
    public async Task A_server_that_never_answers_fails_into_retry_instead_of_hanging()
    {
        // The server goes silent before the headers, so the read deadline never arms and only
        // the header deadline can end this.
        var options = new TestFileServerOptions { StallBeforeHeaders = TimeSpan.FromSeconds(5) };
        await using var server = await TestFileServer.StartAsync(Payload(1000), options);
        using var temp = new TempDir();
        var (_, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var shortTimeout = RetryOptions.Default with { HeaderTimeout = TimeSpan.FromMilliseconds(200) };
        var fetcher = new ChunkFetcher(client, shortTimeout, new FakeDelayProvider());

        using (target)
        {
            var ex = await Assert.ThrowsAsync<IOException>(
                () => fetcher.FetchAsync(new Chunk(0, 0, 100), new[] { Mirror(server.FileUrl) }, 0,
                    0, target.Handle, null, CancellationToken.None));

            Assert.Contains("sent no response headers", ex.Message);
        }

        Assert.Equal(RetryOptions.Default.MaxAttempts, server.RequestCount);
    }

    [Fact]
    public async Task Rejects_a_206_that_serves_a_different_range_than_was_asked_for()
    {
        // The status says 206 and the body is the right length, so nothing but Content-Range
        // reveals that these are the wrong bytes. Written at the chunk's offset they would be
        // flagged complete and trusted by every later resume.
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { MisreportContentRange = true });
        using var temp = new TempDir();
        var (path, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        using (target)
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => fetcher.FetchAsync(new Chunk(1, 100, 100), new[] { Mirror(server.FileUrl) }, 0,
                    0, target.Handle, null, CancellationToken.None));

            Assert.Contains("Content-Range", ex.Message);
        }

        // Nothing was written: the chunk's own region of the part file is still zeroed.
        var written = await File.ReadAllBytesAsync(path);
        Assert.All(written[100..200], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Throws_after_exhausting_every_attempt()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(1000), new TestFileServerOptions { FailNextRequests = 1000 });
        using var temp = new TempDir();
        var (_, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());

        using (target)
            await Assert.ThrowsAsync<HttpRequestException>(
                () => fetcher.FetchAsync(new Chunk(0, 0, 100), new[] { Mirror(server.FileUrl) }, 0,
                    0, target.Handle, null, CancellationToken.None));

        Assert.Equal(RetryOptions.Default.MaxAttempts, server.RequestCount);
    }

    [Fact]
    public async Task Abandons_the_reporting_workers_provisional_bytes_on_a_validator_mismatch()
    {
        // A single FetchAsync rejects before reading a byte, so the mismatch has zero bytes to
        // abandon; the worker index is the part that matters. Two chunks run on the same worker
        // here so that abandoning the wrong slot double-counts a retry.
        var content = Payload(1000);
        var options = new TestFileServerOptions { ETag = "\"v1\"" };
        await using var server = await TestFileServer.StartAsync(content, options);
        using var temp = new TempDir();
        var (_, target) = PreparedTarget(temp, 1000);
        using var client = HttpFactory.Create(4);
        var fetcher = new ChunkFetcher(client, RetryOptions.Default, new FakeDelayProvider());
        var recorder = new RecordingProgress();
        const int worker = 5;

        using (target)
        {
            // A normal fetch, validator matches: reads land and are reported for `worker`.
            await fetcher.FetchAsync(new Chunk(0, 0, 100), new[] { Mirror(server.FileUrl, "\"v1\"") }, 0,
                worker, target.Handle, recorder, CancellationToken.None);

            Assert.NotEmpty(recorder.Advances);
            Assert.All(recorder.Advances, a => Assert.Equal(worker, a.Worker));
            Assert.Empty(recorder.Abandons);

            // The entity changes underneath the download: the mirror's validator is now stale.
            options.ETag = "\"v2\"";

            await Assert.ThrowsAsync<ValidatorMismatchException>(
                () => fetcher.FetchAsync(new Chunk(1, 100, 100), new[] { Mirror(server.FileUrl, "\"v1\"") }, 0,
                    worker, target.Handle, recorder, CancellationToken.None));
        }

        Assert.Equal(new[] { worker }, recorder.Abandons);
    }
}
