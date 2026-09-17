using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

/// <summary>
/// The resolver turns a GoFile share into the concrete per-host URLs the engine downloads from,
/// and installs the token those URLs are bound to. Nothing here contacts the real service.
/// </summary>
public class GoFileResolverTests
{
    private static HttpClient ClientFor(CookieContainer cookies) =>
        new(new SocketsHttpHandler { CookieContainer = cookies, UseCookies = true });

    private static DownloadRequest Request(params string[] urls) =>
        new("EP01", urls.Select(u => new Uri(u)).ToArray(), 1024, new string('a', 64));

    private static GoFileContent OneArchive(long size = 1024) => new()
    {
        Files =
        {
            new GoFileFile
            {
                Id = "file-1", Name = "EP01.zip", Size = size,
                Servers = new() { "file-ap-sgp-3", "file-eu-par-2", "store10" },
            },
        },
    };

    [Fact]
    public async Task A_share_resolves_to_one_url_per_host_serving_it()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var resolved = await resolver.ResolveAsync(
            Request("https://gofile.io/d/abc123"), CancellationToken.None);

        // Three hosts in, three URLs out, each naming a different host and all naming the same
        // file. A resolver that returned only the link the listing selected would give the engine
        // one mirror where six are available, and mirror rotation would have nothing to rotate to.
        Assert.Equal(3, resolved.Urls.Count);
        Assert.Equal(
            new[] { "file-ap-sgp-3", "file-eu-par-2", "store10" },
            resolved.Urls.Select(u => u.Host.Split('.')[0]).OrderBy(h => h).ToArray());
        Assert.All(resolved.Urls, u => Assert.EndsWith("/file-1/EP01.zip", u.AbsolutePath));
    }

    [Fact]
    public async Task The_selected_host_is_resolved_first()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = new GoFileContent
        {
            Files =
            {
                new GoFileFile
                {
                    Id = "file-1", Name = "EP01.zip", Size = 1024,
                    Servers = new() { "file-sa-sao-1", "store10", "file-eu-par-2" },
                },
            },
        };

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var resolved = await resolver.ResolveAsync(
            Request("https://gofile.io/d/abc123"), CancellationToken.None);

        // Worker 0 starts at mirror 0, so the host GoFile picked for this client must be first
        // rather than merely present: the API orders the list by its own proximity estimate.
        Assert.StartsWith("file-sa-sao-1.", resolved.Urls[0].Host);
    }

    [Fact]
    public async Task The_token_the_listing_used_is_installed_for_the_download_hosts()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var resolved = await resolver.ResolveAsync(
            Request("https://gofile.io/d/abc123"), CancellationToken.None);

        // The service binds a link to the token that listed it and answers any other token with a
        // 200-and-an-HTML-page rather than an error. Without the cookie on the download host the
        // engine would write that page into the archive, so this is the whole reason the resolver
        // touches the cookie container at all.
        var sent = cookies.GetCookies(resolved.Urls[0]).Cast<Cookie>().ToArray();
        var token = Assert.Single(sent, c => c.Name == "accountToken");
        Assert.True(api.WasIssued(token.Value), $"'{token.Value}' was never issued by the API.");
    }

    [Fact]
    public async Task A_non_gofile_url_is_passed_through_untouched()
    {
        await using var api = await TestGoFileServer.StartAsync();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var request = Request("https://minio.example.invalid/sims/EP01.zip");
        var resolved = await resolver.ResolveAsync(request, CancellationToken.None);

        // Every catalog entry that is not a GoFile share must reach the engine exactly as the
        // catalog wrote it, and must cost no API call: the overwhelming majority of entries are
        // plain mirrors, and a resolver that dialled GoFile for them would put the whole catalog
        // behind a third-party service it does not use.
        Assert.Equal(request.Urls, resolved.Urls);
        Assert.Equal(0, api.TokensIssued);
    }

    [Fact]
    public async Task A_mixed_entry_resolves_the_share_and_keeps_the_plain_mirror()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var resolved = await resolver.ResolveAsync(
            Request("https://gofile.io/d/abc123", "https://minio.example.invalid/sims/EP01.zip"),
            CancellationToken.None);

        // A pack listing both kinds gets both: the mirrors are alternatives to each other, and
        // dropping the plain one would throw away the mirror that still works when GoFile does not.
        Assert.Equal(4, resolved.Urls.Count);
        Assert.Contains(resolved.Urls, u => u.Host == "minio.example.invalid");
        Assert.Equal(3, resolved.Urls.Count(u => u.Host.EndsWith(".example.invalid")
                                                 && u.Host != "minio.example.invalid"));
    }

    [Fact]
    public async Task The_listing_carries_the_website_token_header()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        await resolver.ResolveAsync(Request("https://gofile.io/d/abc123"), CancellationToken.None);

        // The live API answers a listing without this header with "error-notPremium", so a
        // resolver that stopped sending it would fail against the real service while every test
        // that only checks the bearer token stayed green.
        Assert.Equal(0, api.ListingsMissingWebsiteToken);
    }

    [Fact]
    public async Task An_api_error_is_reported_with_the_status_it_gave()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();
        api.NextListingStatus = "error-notPremium";

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => resolver.ResolveAsync(Request("https://gofile.io/d/abc123"), CancellationToken.None));

        // The status is the only thing that distinguishes a rate limit from a removed share from
        // an auth change, and it is what an operator needs to tell them apart.
        Assert.Contains("error-notPremium", error.Message);
    }

    [Fact]
    public async Task A_share_holding_no_archive_is_reported_rather_than_resolving_to_nothing()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["empty"] = new GoFileContent();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => resolver.ResolveAsync(Request("https://gofile.io/d/empty"), CancellationToken.None));

        // Returning an empty url list instead would reach SegmentedDownloader, which probes every
        // url and throws "no mirror could be reached" - naming the mirrors as the fault when the
        // share is what is empty.
        Assert.Contains("empty", error.Message);
        Assert.Contains("no file", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_archive_inside_a_folder_is_found()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["nested"] = new GoFileContent
        {
            NestedFiles =
            {
                new GoFileFile { Id = "file-9", Name = "EP01.zip", Size = 1024, Servers = new() { "store10" } },
            },
        };

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var resolved = await resolver.ResolveAsync(
            Request("https://gofile.io/d/nested"), CancellationToken.None);

        // Uploading a directory rather than a file nests the archive one level down. The four
        // packs adopted here are flat, but nothing stops a future catalog entry pointing at a
        // share that is not, and a resolver that only read the top level would call it empty.
        Assert.Single(resolved.Urls);
        Assert.EndsWith("/file-9/EP01.zip", resolved.Urls[0].AbsolutePath);
    }

    [Fact]
    public async Task Everything_but_the_urls_is_carried_through_unchanged()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive(size: 999);

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var request = Request("https://gofile.io/d/abc123");
        var resolved = await resolver.ResolveAsync(request, CancellationToken.None);

        // The catalog's size and digest stay authoritative. GoFile reports a size of its own and
        // an md5, and a resolver that let either win would move the gate off the value the
        // catalog is signed on: SegmentedDownloader cross-checks Size against the probed entity
        // and ArchiveFinalizer gates completion on Sha256.
        Assert.Equal(request.Code, resolved.Code);
        Assert.Equal(request.Size, resolved.Size);
        Assert.Equal(request.Sha256, resolved.Sha256);
    }

    [Fact]
    public async Task Two_shares_resolved_in_turn_each_get_a_usable_token()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["one"] = OneArchive();
        api.Contents["two"] = OneArchive();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        var first = await resolver.ResolveAsync(Request("https://gofile.io/d/one"), CancellationToken.None);
        var second = await resolver.ResolveAsync(Request("https://gofile.io/d/two"), CancellationToken.None);

        // A queue resolves one pack after another. The cookie is per host, not per share, so the
        // second resolution overwrites the first's cookie - which is correct only because the
        // engine downloads one archive at a time and the earlier pack is finished by then. The
        // token in force must always be one the API issued, never a stale value.
        foreach (var url in first.Urls.Concat(second.Urls))
        {
            var token = cookies.GetCookies(url).Cast<Cookie>().Single(c => c.Name == "accountToken");
            Assert.True(api.WasIssued(token.Value));
        }
    }

    [Fact]
    public async Task One_token_serves_every_share_in_a_run()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["one"] = OneArchive();
        api.Contents["two"] = OneArchive();
        api.Contents["three"] = OneArchive();

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl);

        foreach (var id in new[] { "one", "two", "three" })
            await resolver.ResolveAsync(Request($"https://gofile.io/d/{id}"), CancellationToken.None);

        // One token for the run, not one per pack. Every request counts against the service's rate
        // limit, and a queue resolves a share for every pack the user selected: minting a token
        // each time doubled the calls and a full catalog ran into 429s partway through.
        Assert.Equal(1, api.TokensIssued);
    }

    [Fact]
    public async Task A_rate_limited_listing_is_retried_rather_than_failing_the_pack()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();
        api.FailNextWith429 = 2;

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(
            http, cookies, api.BaseUrl, delay: new FakeDelayProvider());

        var resolved = await resolver.ResolveAsync(
            Request("https://gofile.io/d/abc123"), CancellationToken.None);

        // A 429 says "later", not "never". Without a retry every pack after the limit is reached
        // fails outright, which is what a full-catalog run actually did: 48 of 116 packs threw on
        // the listing while nothing was wrong with any of them.
        Assert.Equal(3, resolved.Urls.Count);
    }

    [Fact]
    public async Task A_persistent_rate_limit_is_reported_as_such()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();
        api.FailNextWith429 = 99;

        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(
            http, cookies, api.BaseUrl, delay: new FakeDelayProvider());

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => resolver.ResolveAsync(Request("https://gofile.io/d/abc123"), CancellationToken.None));

        // Named, because "429" in a log is the difference between "wait and try again" and "this
        // pack is gone", and the user's next action differs entirely.
        Assert.Contains("rate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_retry_after_header_is_honoured()
    {
        await using var api = await TestGoFileServer.StartAsync();
        api.Contents["abc123"] = OneArchive();
        api.FailNextWith429 = 1;
        api.RetryAfterSeconds = 7;

        var delays = new FakeDelayProvider();
        var cookies = new CookieContainer();
        using var http = ClientFor(cookies);
        var resolver = new GoFileResolver(http, cookies, api.BaseUrl, delay: delays);

        await resolver.ResolveAsync(Request("https://gofile.io/d/abc123"), CancellationToken.None);

        // The service says how long to wait; guessing shorter earns another 429 and guessing
        // longer stalls the queue for no reason.
        Assert.Contains(delays.Delays, d => d == TimeSpan.FromSeconds(7));
    }

    [Theory]
    [InlineData("https://gofile.io/d/abc123", true)]
    [InlineData("https://gofile.io/contents/abc123", true)]
    [InlineData("https://www.gofile.io/d/abc123", true)]
    [InlineData("https://minio.example.invalid/sims/EP01.zip", false)]
    [InlineData("https://notgofile.io/d/abc123", false)]
    [InlineData("https://gofile.io.example.invalid/d/abc123", false)]
    public void A_share_is_recognised_by_host_not_by_substring(string url, bool expected)
    {
        // "gofile.io.example.invalid" is the case that matters: a substring match on the whole
        // url would treat an attacker-controlled host as a share and send it the account token.
        Assert.Equal(expected, GoFileResolver.IsShare(new Uri(url)));
    }
}
