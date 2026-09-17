using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Net;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class HttpFactoryTests
{
    [Fact]
    public void Allows_multiple_http2_connections()
    {
        // Without this, an HTTP/2 host multiplexes every range request over one TCP
        // connection, reproducing exactly the single-connection bottleneck being fixed.
        using var handler = HttpFactory.CreateHandler(8);

        Assert.True(handler.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void Sets_the_connection_ceiling_from_the_argument()
    {
        using var handler = HttpFactory.CreateHandler(12);

        Assert.Equal(12, handler.MaxConnectionsPerServer);
    }

    [Fact]
    public void Disables_automatic_decompression()
    {
        // Transparent decompression would break byte-range accounting.
        using var handler = HttpFactory.CreateHandler(8);

        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public void Bounds_pooled_connection_lifetime()
    {
        using var handler = HttpFactory.CreateHandler(8);

        Assert.InRange(handler.PooledConnectionLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Client_has_no_whole_operation_timeout()
    {
        // A multi-gigabyte transfer must not race a global clock. Stalls are caught instead by
        // RetryOptions.ReadTimeout inside ChunkFetcher and SingleStreamDownloader.
        using var client = HttpFactory.Create(8);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public async Task The_shared_client_admits_the_largest_connection_count_the_engine_allows()
    {
        // Connections is a live setting and a handler's ceiling is fixed for its lifetime, so the
        // pool is sized for DownloadOptions.MaxConnections. Sized for the startup value, raising
        // the setting mid-session would leave the extra workers queued in the pool.
        //
        // Real requests are driven here because asserting on the handler would only restate the
        // argument passed to it.
        var payload = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        await using var server = await TestFileServer.StartAsync(
            payload, new TestFileServerOptions { StallBeforeHeaders = TimeSpan.FromMilliseconds(400) });
        using var client = HttpFactory.Create();

        await Task.WhenAll(Enumerable.Range(0, DownloadOptions.MaxConnections)
            .Select(_ => client.GetByteArrayAsync(server.FileUrl)));

        Assert.Equal(DownloadOptions.MaxConnections, server.RequestCount);
        Assert.Equal(DownloadOptions.MaxConnections, server.PeakConcurrentRequests);
    }
}
