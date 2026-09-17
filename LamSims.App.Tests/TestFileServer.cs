using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LamSims.App.Tests;

public sealed class TestFileServerOptions
{
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>When false, range requests are answered with a 200 and the whole body.</summary>
    public bool SupportRanges { get; set; } = true;

    /// <summary>When false, no Accept-Ranges header is sent even though ranges work.</summary>
    public bool AdvertiseAcceptRanges { get; set; } = true;

    /// <summary>
    /// Answer range requests with a 206 whose Content-Range covers the whole entity instead
    /// of the bytes asked for, as some intermediaries do. The body is still the requested
    /// slice, so only the header betrays it.
    /// </summary>
    public bool MisreportContentRange { get; set; }

    public string? ETag { get; set; } = "\"v1\"";
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Answer this many requests with 503 before serving normally. Decremented per request.</summary>
    public int FailNextRequests { get; set; }

    /// <summary>Abort the response after writing this many bytes of body.</summary>
    public long? DropAfterBytes { get; set; }

    /// <summary>
    /// How many responses DropAfterBytes applies to, decremented each time one is dropped.
    /// Set to 1 for "drop the first response, then behave", which keeps recovery tests
    /// deterministic instead of racing a timer against instant retries.
    /// </summary>
    public int DropAfterBytesCount { get; set; } = int.MaxValue;

    /// <summary>
    /// Write this many bytes of the body, then stop sending and wait <see cref="StallFor"/>
    /// before writing the rest (or, if null, before the request is aborted). Simulates a
    /// mirror that completes its handshake and headers, then stops delivering bytes, which is
    /// what RetryOptions.ReadTimeout exists to catch.
    /// </summary>
    public long? StallAfterBytes { get; set; }

    /// <summary>
    /// How long to stall for. Null means 30 seconds, long enough that the client's own read
    /// timeout fires first. Any stall ends early once the client gives up.
    /// </summary>
    public TimeSpan? StallFor { get; set; }

    /// <summary>
    /// Run where <see cref="StallAfterBytes"/> stops sending, before the stall and before the rest
    /// of the body. A test that has to arrange something while a transfer is in flight uses it, so
    /// the arrangement is ordered against the transfer rather than racing it. Called on the
    /// request's own thread, once per stalled request.
    /// </summary>
    public Action? AtStall { get; set; }

    /// <summary>
    /// Ends every stall at once, without writing the rest of the body. A stalled handler otherwise
    /// waits for the client to abort, and a client that considers its response complete never
    /// does — so disposing the server means waiting out the host's shutdown timeout with the
    /// handler still parked. A test that cancels its own transfer signals this instead.
    /// </summary>
    public CancellationToken ReleaseStall { get; set; }

    /// <summary>
    /// Wait this long before writing anything at all, so the client sees a completed
    /// handshake and no response headers. Applies to every request, and ends early once the
    /// client gives up. Simulates the silence RetryOptions.HeaderTimeout exists to catch.
    /// </summary>
    public TimeSpan? StallBeforeHeaders { get; set; }
}

/// <summary>
/// A Kestrel server that serves one file and can misbehave on demand: ignore ranges,
/// hide Accept-Ranges, return 503s, drop connections mid-body, or change its validator.
/// </summary>
public sealed class TestFileServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string?> _rangeHeaders = new();
    private readonly ConcurrentQueue<string?> _ifRangeHeaders = new();
    private int _requestCount;
    private int _inFlight;
    private int _peakInFlight;

    public TestFileServerOptions Options { get; }
    public Uri BaseUrl { get; }
    public Uri FileUrl => new(BaseUrl, "file.zip");
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>The most requests ever being served at the same moment.</summary>
    public int PeakConcurrentRequests => Volatile.Read(ref _peakInFlight);
    public IReadOnlyList<string?> ReceivedRangeHeaders => _rangeHeaders.ToArray();
    public IReadOnlyList<string?> ReceivedIfRangeHeaders => _ifRangeHeaders.ToArray();

    private TestFileServer(WebApplication app, TestFileServerOptions options, Uri baseUrl)
    {
        _app = app;
        Options = options;
        BaseUrl = baseUrl;
    }

    public void ResetCounters()
    {
        Volatile.Write(ref _requestCount, 0);
        Volatile.Write(ref _peakInFlight, 0);
        _rangeHeaders.Clear();
        _ifRangeHeaders.Clear();
    }

    public static async Task<TestFileServer> StartAsync(byte[] content, TestFileServerOptions? options = null)
    {
        options ??= new TestFileServerOptions();
        options.Content = content;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        TestFileServer? server = null;

        app.MapGet("/file.zip", async (HttpContext context) =>
        {
            var self = server!;
            Interlocked.Increment(ref self._requestCount);

            var inFlight = Interlocked.Increment(ref self._inFlight);
            InterlockedRaise(ref self._peakInFlight, inFlight);
            try
            {
                await ServeAsync(self, context);
            }
            finally
            {
                Interlocked.Decrement(ref self._inFlight);
            }
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        server = new TestFileServer(app, options, new Uri(address.TrimEnd('/') + "/"));
        return server;
    }

    private static void InterlockedRaise(ref int target, int candidate)
    {
        var seen = Volatile.Read(ref target);
        while (candidate > seen)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, seen);
            if (previous == seen) return;
            seen = previous;
        }
    }

    private static async Task ServeAsync(TestFileServer self, HttpContext context)
    {
        // Both header queues are recorded here, before any early return, so they stay
        // index-aligned with each other and with RequestCount even when a request is
        // answered with a 503.
        var rangeHeader = context.Request.Headers.Range.ToString();
        self._rangeHeaders.Enqueue(string.IsNullOrEmpty(rangeHeader) ? null : rangeHeader);
        var ifRange = context.Request.Headers["If-Range"].ToString();
        self._ifRangeHeaders.Enqueue(string.IsNullOrEmpty(ifRange) ? null : ifRange);

        var opts = self.Options;

        if (opts.StallBeforeHeaders is { } headerStall)
        {
            try
            {
                await Task.Delay(headerStall, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // The client's header deadline fired and it abandoned the request, which
                // is exactly the scenario under test.
                return;
            }
        }

        if (opts.FailNextRequests > 0)
        {
            opts.FailNextRequests--;
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return;
        }

        var content = opts.Content;

        if (opts.ETag is not null) context.Response.Headers.ETag = opts.ETag;
        if (opts.LastModified is not null)
            context.Response.Headers.LastModified = opts.LastModified.Value.ToString("R");
        if (opts.AdvertiseAcceptRanges) context.Response.Headers.AcceptRanges = "bytes";

        var validatorMatches = string.IsNullOrEmpty(ifRange)
            || ifRange == opts.ETag
            || (opts.LastModified is not null && ifRange == opts.LastModified.Value.ToString("R"));

        var (from, to) = ParseRange(rangeHeader, content.LongLength);
        var serveRange = opts.SupportRanges && from >= 0 && validatorMatches;

        byte[] body;
        if (serveRange)
        {
            context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
            context.Response.Headers.ContentRange = opts.MisreportContentRange
                ? $"bytes 0-{content.LongLength - 1}/{content.LongLength}"
                : $"bytes {from}-{to}/{content.LongLength}";
            body = content[(int)from..(int)(to + 1)];
        }
        else
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            body = content;
        }

        context.Response.ContentLength = body.LongLength;

        if (opts.DropAfterBytes is { } dropAfter && dropAfter < body.LongLength
            && opts.DropAfterBytesCount > 0)
        {
            opts.DropAfterBytesCount--;
            await context.Response.Body.WriteAsync(body.AsMemory(0, (int)dropAfter));
            await context.Response.Body.FlushAsync();
            context.Abort();
            return;
        }

        if (opts.StallAfterBytes is { } stallAfter && stallAfter <= body.LongLength)
        {
            await context.Response.Body.WriteAsync(body.AsMemory(0, (int)stallAfter));
            await context.Response.Body.FlushAsync();

            opts.AtStall?.Invoke();

            using var release = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, opts.ReleaseStall);

            try
            {
                // Bounded even when the test leaves StallFor unset, so a client that
                // somehow never aborts cannot hang the server thread forever.
                await Task.Delay(opts.StallFor ?? TimeSpan.FromSeconds(30), release.Token);
            }
            catch (OperationCanceledException)
            {
                // Either the client gave up (its own read timeout fired) and disposed the
                // response, which is the scenario under test, or the test released the stall
                // because it is finished with the transfer.
                return;
            }

            await context.Response.Body.WriteAsync(body.AsMemory((int)stallAfter));
            return;
        }

        await context.Response.Body.WriteAsync(body);
    }

    /// <summary>Parses "bytes=from-to". Returns (-1, -1) when absent or unsatisfiable.</summary>
    private static (long From, long To) ParseRange(string? header, long totalLength)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.Ordinal))
            return (-1, -1);

        var spec = header["bytes=".Length..];
        var dash = spec.IndexOf('-');
        if (dash < 0) return (-1, -1);

        if (!long.TryParse(spec[..dash], out var from)) return (-1, -1);
        var toText = spec[(dash + 1)..];
        var to = string.IsNullOrEmpty(toText) ? totalLength - 1 : long.Parse(toText);

        if (from >= totalLength) return (-1, -1);
        if (to >= totalLength) to = totalLength - 1;
        return (from, to);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
