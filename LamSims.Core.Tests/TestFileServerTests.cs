using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using System.Net;
using System.Net.Http.Headers;

namespace LamSims.Core.Tests;

public class TestFileServerTests
{
    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    [Fact]
    public async Task Serves_the_whole_file_on_a_plain_get()
    {
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var client = new HttpClient();

        var response = await client.GetAsync(server.FileUrl);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, body);
        Assert.Equal(1000, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Serves_a_range_as_206_with_content_range()
    {
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        request.Headers.Range = new RangeHeaderValue(100, 199);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(content[100..200], body);
        Assert.Equal(100, response.Content.Headers.ContentRange!.From);
        Assert.Equal(199, response.Content.Headers.ContentRange.To);
        Assert.Equal(1000, response.Content.Headers.ContentRange.Length);
    }

    [Fact]
    public async Task A_zero_zero_probe_reports_the_total_length()
    {
        await using var server = await TestFileServer.StartAsync(Payload(4096));
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(1, response.Content.Headers.ContentLength);
        Assert.Equal(4096, response.Content.Headers.ContentRange!.Length);
    }

    [Fact]
    public async Task Ignores_ranges_when_configured_to()
    {
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { SupportRanges = false });
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        request.Headers.Range = new RangeHeaderValue(100, 199);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Can_omit_accept_ranges_while_still_honouring_ranges()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(1000), new TestFileServerOptions { AdvertiseAcceptRanges = false });
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Empty(response.Headers.AcceptRanges);
    }

    [Fact]
    public async Task Fails_the_configured_number_of_requests_then_succeeds()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(100), new TestFileServerOptions { FailNextRequests = 2 });
        using var client = new HttpClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(server.FileUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(server.FileUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(server.FileUrl)).StatusCode);
    }

    [Fact]
    public async Task Drops_the_connection_mid_body()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(100_000), new TestFileServerOptions { DropAfterBytes = 1000 });
        using var client = new HttpClient();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var response = await client.GetAsync(server.FileUrl, HttpCompletionOption.ResponseHeadersRead);
            await response.Content.ReadAsByteArrayAsync();
        });
    }

    [Fact]
    public async Task Returns_200_when_the_if_range_validator_does_not_match()
    {
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { ETag = "\"v1\"" });
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        request.Headers.Range = new RangeHeaderValue(100, 199);
        request.Headers.TryAddWithoutValidation("If-Range", "\"stale\"");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Records_the_range_headers_it_received()
    {
        await using var server = await TestFileServer.StartAsync(Payload(1000));
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        await client.SendAsync(request);

        Assert.Equal(new[] { "bytes=10-19" }, server.ReceivedRangeHeaders);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Records_both_header_queues_even_for_a_failed_request()
    {
        await using var server = await TestFileServer.StartAsync(
            Payload(1000), new TestFileServerOptions { FailNextRequests = 1 });
        using var client = new HttpClient();

        using var failing = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        failing.Headers.Range = new RangeHeaderValue(0, 9);
        await client.SendAsync(failing);

        using var succeeding = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        succeeding.Headers.Range = new RangeHeaderValue(10, 19);
        succeeding.Headers.TryAddWithoutValidation("If-Range", "\"v1\"");
        await client.SendAsync(succeeding);

        // Both queues must advance in lockstep, including for the 503, or positional
        // comparisons between them silently compare unrelated requests.
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(new string?[] { "bytes=0-9", "bytes=10-19" }, server.ReceivedRangeHeaders);
        Assert.Equal(new string?[] { null, "\"v1\"" }, server.ReceivedIfRangeHeaders);
    }
}
