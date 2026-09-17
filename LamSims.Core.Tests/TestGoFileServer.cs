using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LamSims.Core.Tests;

/// <summary>
/// A stand-in for the GoFile API, shaped from responses recorded against the live service.
/// Only the three behaviours the resolver depends on are modelled: a guest token is minted per
/// POST /accounts, a content listing names one archive and the hosts serving it, and a listing
/// requires the website token header the real API rejects requests without.
///
/// The download hosts are NOT modelled here. The resolver's job ends at producing URLs and a
/// cookie; fetching them is the engine's, and the engine already has TestFileServer.
/// </summary>
public sealed class TestGoFileServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte> _issuedTokens = new(StringComparer.Ordinal);
    private int _tokensIssued;

    public Uri BaseUrl { get; }

    /// <summary>The content ids this server knows, and the archive each one holds.</summary>
    public Dictionary<string, GoFileContent> Contents { get; } = new(StringComparer.Ordinal);

    /// <summary>Answer the next listing with this API status instead of "ok".</summary>
    public string? NextListingStatus { get; set; }

    /// <summary>
    /// Answer this many requests with 429 before serving normally, as the real API does once a
    /// burst of listings crosses its rate limit. Decremented per request.
    /// </summary>
    public int FailNextWith429 { get; set; }

    /// <summary>Sent as Retry-After on a 429. Null sends no header.</summary>
    public int? RetryAfterSeconds { get; set; }

    /// <summary>Listings served without the X-Website-Token header, which the real API refuses.</summary>
    public int ListingsMissingWebsiteToken => Volatile.Read(ref _listingsMissingWebsiteToken);
    private int _listingsMissingWebsiteToken;

    public int TokensIssued => Volatile.Read(ref _tokensIssued);

    /// <summary>Every bearer token a listing was requested with, in order.</summary>
    public IReadOnlyList<string?> ListingTokens => _listingTokens.ToArray();
    private readonly ConcurrentQueue<string?> _listingTokens = new();

    private TestGoFileServer(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<TestGoFileServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        TestGoFileServer? server = null;

        app.MapPost("/accounts", (HttpContext context) =>
        {
            var self = server!;

            if (self.Throttle(context) is { } throttled) return throttled;
            var token = "tok" + Interlocked.Increment(ref self._tokensIssued);
            self._issuedTokens[token] = 0;

            return Results.Json(new
            {
                status = "ok",
                data = new { id = "acc", rootFolder = "root", tier = "guest", token },
            });
        });

        app.MapGet("/contents/{id}", (HttpContext context, string id) =>
        {
            var self = server!;

            if (self.Throttle(context) is { } throttled) return throttled;

            if (string.IsNullOrEmpty(context.Request.Headers["X-Website-Token"].ToString()))
                Interlocked.Increment(ref self._listingsMissingWebsiteToken);

            var bearer = context.Request.Headers.Authorization.ToString();
            self._listingTokens.Enqueue(string.IsNullOrEmpty(bearer) ? null : bearer);

            if (self.NextListingStatus is { } status)
            {
                self.NextListingStatus = null;
                return Results.Json(new { status, data = new { } });
            }

            if (!self.Contents.TryGetValue(id, out var content))
                return Results.Json(new { status = "error-notFound", data = new { } });

            return Results.Json(new
            {
                status = "ok",
                data = new
                {
                    canAccess = true,
                    id = "folder-" + id,
                    type = "folder",
                    name = id,
                    code = id,
                    children = content.ToChildren(self.BaseUrl),
                },
            });
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        server = new TestGoFileServer(app, new Uri(address.TrimEnd('/') + "/"));
        return server;
    }

    /// <summary>The 429 the real service sends when a burst of listings crosses its rate limit.</summary>
    private IResult? Throttle(HttpContext context)
    {
        if (FailNextWith429 <= 0) return null;

        FailNextWith429--;

        if (RetryAfterSeconds is { } after)
            context.Response.Headers.RetryAfter = after.ToString();

        return Results.StatusCode(429);
    }

    public bool WasIssued(string token) => _issuedTokens.ContainsKey(token);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>What one content id holds. Empty <see cref="Files"/> models a share whose archive
/// has been removed, which the resolver must report rather than return nothing for.</summary>
public sealed class GoFileContent
{
    public List<GoFileFile> Files { get; init; } = new();

    /// <summary>A folder nested inside the share, as a GoFile upload of a directory produces.</summary>
    public List<GoFileFile> NestedFiles { get; init; } = new();

    internal object ToChildren(Uri baseUrl)
    {
        var children = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var file in Files)
            children[file.Id] = file.ToNode(baseUrl);

        if (NestedFiles.Count > 0)
        {
            var nested = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var file in NestedFiles)
                nested[file.Id] = file.ToNode(baseUrl);

            children["folder-nested"] = new
            {
                id = "folder-nested",
                type = "folder",
                name = "nested",
                children = nested,
            };
        }

        return children;
    }
}

public sealed class GoFileFile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public string Md5 { get; init; } = new('0', 32);

    /// <summary>
    /// The hosts the real API lists for a file. The resolver turns each into one url.
    ///
    /// No default: `Servers = { ... }` in an object initializer ADDS to whatever list is already
    /// there rather than replacing it, so a default would silently append to every fixture and a
    /// test naming one host would quietly get three.
    /// </summary>
    public required List<string> Servers { get; init; }

    /// <summary>Overrides the link the listing reports, for the case where it names a host that
    /// is not in <see cref="Servers"/>.</summary>
    public string? Link { get; init; }

    internal object ToNode(Uri baseUrl) => new
    {
        id = Id,
        type = "file",
        name = Name,
        size = Size,
        md5 = Md5,
        servers = Servers,
        serverSelected = Servers.FirstOrDefault() ?? "store10",
        link = Link ?? $"{baseUrl.Scheme}://{Servers.FirstOrDefault() ?? "store10"}.example.invalid/download/web/{Id}/{Name}",
    };
}
