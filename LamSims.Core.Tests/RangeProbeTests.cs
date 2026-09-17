using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class RangeProbeTests
{
    private static byte[] Payload(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task Reads_the_total_size_from_content_range_not_content_length()
    {
        // The probe response body is one byte, so Content-Length is 1; the real size
        // lives in Content-Range: bytes 0-0/N.
        await using var server = await TestFileServer.StartAsync(Payload(4096));
        using var client = HttpFactory.Create(4);

        var result = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);

        Assert.Equal(RangeSupport.Supported, result.RangeSupport);
        Assert.Equal(4096, result.TotalSize);
    }

    [Fact(Timeout = 15000)]
    public async Task A_server_that_never_answers_fails_instead_of_hanging()
    {
        // The probe runs before any worker starts, so an unbounded wait here stalls the whole
        // download with nothing to show for it.
        await using var server = await TestFileServer.StartAsync(
            Payload(4096), new TestFileServerOptions { StallBeforeHeaders = TimeSpan.FromSeconds(5) });
        using var client = HttpFactory.Create(4);

        var ex = await Assert.ThrowsAsync<IOException>(
            () => new RangeProbe(client, TimeSpan.FromMilliseconds(200))
                .ProbeAsync(server.FileUrl, CancellationToken.None));

        Assert.Contains("sent no response headers", ex.Message);
    }

    [Fact]
    public async Task Treats_a_200_response_as_no_range_support()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(4096), new TestFileServerOptions { SupportRanges = false });
        using var client = HttpFactory.Create(4);

        var result = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);

        Assert.Equal(RangeSupport.NotSupported, result.RangeSupport);
        Assert.Equal(4096, result.TotalSize);
    }

    [Fact]
    public async Task Trusts_the_206_status_even_when_accept_ranges_is_absent()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(4096), new TestFileServerOptions { AdvertiseAcceptRanges = false });
        using var client = HttpFactory.Create(4);

        var result = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);

        Assert.Equal(RangeSupport.Supported, result.RangeSupport);
    }

    [Fact]
    public async Task Captures_a_strong_etag_as_the_validator()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(100), new TestFileServerOptions { ETag = "\"abc\"" });
        using var client = HttpFactory.Create(4);

        var result = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);

        Assert.Equal("\"abc\"", result.Validator.ETag);
        Assert.True(result.Validator.HasValidator);
    }

    [Fact]
    public async Task Falls_back_to_last_modified_when_the_etag_is_weak()
    {
        var lastModified = new DateTimeOffset(2015, 10, 21, 7, 28, 0, TimeSpan.Zero);
        await using var server = await TestFileServer.StartAsync(
            Payload(100), new TestFileServerOptions { ETag = "W/\"abc\"", LastModified = lastModified });
        using var client = HttpFactory.Create(4);

        var result = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);

        Assert.True(result.Validator.HasValidator);
        Assert.Equal(lastModified.ToString("R"), result.Validator.IfRangeValue);
    }

    [Fact]
    public async Task A_mirror_offering_nothing_is_still_usable()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(100), new TestFileServerOptions { ETag = null });
        using var client = HttpFactory.Create(4);

        var result = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);

        Assert.False(result.Validator.HasValidator);
        Assert.Equal(RangeSupport.Supported, result.RangeSupport);
    }

    [Fact]
    public async Task An_error_response_throws()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(100), new TestFileServerOptions { FailNextRequests = 1 });
        using var client = HttpFactory.Create(4);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None));
    }
}
