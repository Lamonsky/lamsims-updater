using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Logging;

namespace LamSims.Core.Downloading;

/// <summary>
/// Turns a catalog entry's urls into the ones the engine will actually fetch. Most entries need
/// nothing done to them, so the pass-through implementation is the normal case and exists to keep
/// the call site in <see cref="PackWorkflow"/> unconditional.
/// </summary>
public interface IMirrorResolver
{
    Task<DownloadRequest> ResolveAsync(DownloadRequest request, CancellationToken ct);
}

/// <summary>Hands the request back as it came. Every url a catalog names is already fetchable.</summary>
public sealed class PassThroughMirrorResolver : IMirrorResolver
{
    public static readonly PassThroughMirrorResolver Instance = new();

    public Task<DownloadRequest> ResolveAsync(DownloadRequest request, CancellationToken ct) =>
        Task.FromResult(request);
}

/// <summary>
/// Expands a GoFile share into the per-host urls that serve its archive.
///
/// A share url names a listing, not a file, so it cannot be fetched: the concrete urls exist only
/// in the API's answer. Worse, the service binds those urls to the guest token that listed them
/// and answers a request carrying any other token - or none - with an HTML page under status 200
/// rather than an error. Nothing downstream would notice: the engine would write that page into
/// the archive and only the final digest would object, hours later.
///
/// So the token this class mints is installed as a cookie for the download hosts before the urls
/// are handed on. <see cref="ChunkFetcher"/> is the backstop: the hosts send Last-Modified, so a
/// mirror answering a conditional range request with 200 is set aside as a validator mismatch and
/// the HTML never reaches the file.
///
/// Anything that is not a share passes through untouched and costs no API call.
/// </summary>
public sealed class GoFileResolver : IMirrorResolver
{
    private const string PublicApi = "https://api.gofile.io/";

    /// <summary>The literal the website token is salted with. It is a constant of the service's
    /// own client, not a secret of ours, and it changes when the service changes it.</summary>
    private const string TokenSalt = "12af056dacea0b";

    /// <summary>The window the website token is bucketed into, in seconds.</summary>
    private const long TokenSlotSeconds = 14400;

    private const string UserAgent = "Mozilla/5.0";

    /// <summary>Attempts at a listing the service rate-limited. A 429 says "later", not "never".</summary>
    private const int RateLimitAttempts = 5;

    /// <summary>Used when a 429 carries no Retry-After, and as the floor when it carries a short one.</summary>
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly Uri _api;
    private readonly IDelayProvider _delay;
    private readonly ILogSink _log;

    /// <summary>
    /// The guest token for this resolver's lifetime. Every API call counts against the service's
    /// rate limit and a queue resolves one share per selected pack, so a token minted per pack
    /// doubled the calls: a full 116-pack catalog ran into 429s after about 68 of them.
    /// </summary>
    private string? _token;

    /// <param name="cookies">The container backing <paramref name="http"/>. Required rather than
    /// discovered: a handler's container cannot be read back from an HttpClient, and a resolver
    /// that silently skipped the cookie would download an HTML page into the archive.</param>
    /// <param name="api">Where the API lives. Tests point this at a local stand-in; production
    /// uses the default, so no caller has to name the public host.</param>
    public GoFileResolver(
        HttpClient http, CookieContainer cookies, Uri? api = null, ILogSink? log = null,
        IDelayProvider? delay = null)
    {
        _http = http;
        _cookies = cookies;
        _api = api ?? new Uri(PublicApi);
        _delay = delay ?? new SystemDelayProvider();
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>
    /// Whether this url is a GoFile share. Matched on the HOST, never on the url as a whole: a
    /// substring test would accept 'gofile.io.example.invalid' and send an attacker's host the
    /// account token.
    /// </summary>
    public static bool IsShare(Uri url) =>
        url.Host.Equals("gofile.io", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith(".gofile.io", StringComparison.OrdinalIgnoreCase);

    public async Task<DownloadRequest> ResolveAsync(DownloadRequest request, CancellationToken ct)
    {
        if (!request.Urls.Any(IsShare)) return request;

        var resolved = new List<Uri>();

        foreach (var url in request.Urls)
        {
            if (!IsShare(url))
            {
                resolved.Add(url);
                continue;
            }

            resolved.AddRange(await ExpandAsync(url, request.Code, ct));
        }

        return request with { Urls = resolved };
    }

    private async Task<IReadOnlyList<Uri>> ExpandAsync(Uri share, string code, CancellationToken ct)
    {
        var contentId = ContentId(share)
            ?? throw new HttpRequestException($"'{share}' names no GoFile content id.");

        var token = _token ??= await GuestTokenAsync(ct);

        using var listing = await ListAsync(contentId, token, ct);

        var file = FirstFile(listing.RootElement.GetProperty("data"))
            ?? throw new HttpRequestException(
                $"The GoFile share '{contentId}' holds no file, so there is nothing to download.");

        var urls = HostUrls(file);

        if (urls.Count == 0)
            throw new HttpRequestException($"The GoFile share '{contentId}' named no host serving its file.");

        // Per host rather than per url: the cookie container keys on host and path, and every url
        // from one share shares a path prefix. Installed before the urls are returned, so the
        // engine's first probe already carries it.
        foreach (var host in urls.Select(u => u.Host).Distinct(StringComparer.OrdinalIgnoreCase))
            _cookies.Add(new Cookie("accountToken", token, "/", host));

        _log.Write(LogLine.Info(
            $"Resolved the GoFile share {contentId} to {urls.Count} " +
            $"{(urls.Count == 1 ? "host" : "hosts")}", code));

        return urls;
    }

    /// <summary>
    /// The content id from either shape the service publishes: /d/&lt;id&gt; on the site and
    /// /contents/&lt;id&gt; in the API.
    /// </summary>
    private static string? ContentId(Uri share)
    {
        var segments = share.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("d", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("contents", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    private async Task<string> GuestTokenAsync(CancellationToken ct)
    {
        using var document = await SendAsync(
            "a guest token",
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_api, "accounts"));
                Sign(request, accountToken: string.Empty);
                return request;
            },
            ct);

        EnsureOk(document.RootElement);

        return document.RootElement.GetProperty("data").GetProperty("token").GetString()
            ?? throw new HttpRequestException("GoFile issued no account token.");
    }

    private async Task<JsonDocument> ListAsync(string contentId, string token, CancellationToken ct)
    {
        var url = new Uri(_api,
            $"contents/{Uri.EscapeDataString(contentId)}?cache=true&sortField=createTime&sortDirection=1");

        var document = await SendAsync($"the share '{contentId}'", () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            Sign(request, token);
            return request;
        }, ct);

        try
        {
            EnsureOk(document.RootElement);
        }
        catch
        {
            document.Dispose();
            throw;
        }

        return document;
    }

    /// <summary>
    /// One API call, retried while the service is rate limiting. Both calls go through here: a
    /// queue resolving a share per pack makes two calls per pack, and the limit does not care
    /// which of them crosses it.
    ///
    /// <paramref name="build"/> rather than a message, because an HttpRequestMessage cannot be
    /// sent twice and each attempt needs a fresh one.
    /// </summary>
    private async Task<JsonDocument> SendAsync(
        string what, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();

            using var response = await HttpDeadline.SendAsync(
                _http, request, HttpDeadline.DefaultHeaderTimeout, ct);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                response.EnsureSuccessStatusCode();
                return await ReadJsonAsync(response, request.RequestUri!, ct);
            }

            if (attempt >= RateLimitAttempts)
            {
                throw new HttpRequestException(
                    $"GoFile is rate limiting this client: {what} was refused with 429 on "
                    + $"{RateLimitAttempts} attempts. Waiting a few minutes and running again "
                    + "should clear it.");
            }

            // The service's own wait when it gives one, and a widening backoff when it does not.
            var wait = RetryAfter(response) ?? RateLimitBackoff * attempt;

            _log.Write(LogLine.Warning(
                $"GoFile answered 429 for {what}; waiting {wait.TotalSeconds:0}s before attempt "
                + $"{attempt + 1} of {RateLimitAttempts}"));

            await _delay.DelayAsync(wait, ct);
        }
    }

    /// <summary>The service's own wait, when it gives one. Only the delta-seconds form is read;
    /// the HTTP-date form is not something this API sends.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta
        ?? (response.Headers.RetryAfter?.Date is { } date
            ? date - DateTimeOffset.UtcNow
            : null);

    /// <summary>
    /// The headers the API requires of every request. The website token is a hash over the user
    /// agent, the locale, the account token and a four-hour time bucket; the service refuses a
    /// request without it, answering "error-notPremium" rather than anything about the header.
    /// </summary>
    private static void Sign(HttpRequestMessage request, string accountToken)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("X-Website-Token", WebsiteToken(accountToken));
        request.Headers.TryAddWithoutValidation("X-BL", "en-US");
    }

    private static string WebsiteToken(string accountToken)
    {
        var slot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TokenSlotSeconds;
        var raw = $"{UserAgent}::en-US::{accountToken}::{slot}::{TokenSalt}";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, Uri url, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);

        try
        {
            return await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }
        catch (JsonException e)
        {
            throw new HttpRequestException($"GoFile answered '{url}' with content that is not JSON: {e.Message}");
        }
    }

    /// <summary>The status is the only thing separating a rate limit from a removed share from an
    /// auth change, so it is carried into the message rather than collapsed to "failed".</summary>
    private static void EnsureOk(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var value) ? value.GetString() : null;

        if (status != "ok")
            throw new HttpRequestException($"GoFile answered '{status ?? "(no status)"}'.");
    }

    /// <summary>
    /// The first file in the listing, descending into folders. A share holding a directory nests
    /// its archive one level down, and reading only the top level would call such a share empty.
    /// </summary>
    private static JsonElement? FirstFile(JsonElement node)
    {
        if (node.TryGetProperty("type", out var type) && type.GetString() != "folder")
            return node;

        if (!node.TryGetProperty("children", out var children)
            || children.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var child in children.EnumerateObject())
        {
            if (FirstFile(child.Value) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// One url per host the file is on, the host the service selected first. The engine gives
    /// worker N the mirror at index N, so the order decides which host the first bytes come from,
    /// and the service orders the list by its own estimate of what is nearest.
    ///
    /// Spreading across hosts is not an optimisation. One host answers about four concurrent
    /// ranged requests before it starts returning 429; eight requests spread over the hosts of one
    /// file are all served. With a single url the default eight connections would all land on one
    /// host.
    /// </summary>
    private static IReadOnlyList<Uri> HostUrls(JsonElement? file)
    {
        var node = file!.Value;

        var link = node.TryGetProperty("link", out var value) ? value.GetString() : null;
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var template))
            return Array.Empty<Uri>();

        var selected = node.TryGetProperty("serverSelected", out var chosen) ? chosen.GetString() : null;

        var servers = new List<string>();
        if (node.TryGetProperty("servers", out var listed) && listed.ValueKind == JsonValueKind.Array)
        {
            servers.AddRange(listed.EnumerateArray()
                .Select(s => s.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))!);
        }

        // The link's own host is kept whether or not the servers array repeats it: it is the one
        // url the service actually handed out, so it is the one url known to work.
        var linkHost = template.Host.Split('.')[0];
        var ordered = new List<string>();

        foreach (var server in new[] { selected, linkHost }.Concat(servers))
        {
            if (!string.IsNullOrWhiteSpace(server)
                && !ordered.Contains(server!, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(server!);
            }
        }

        var suffix = template.Host[linkHost.Length..];

        return ordered
            .Select(server => new UriBuilder(template) { Host = server + suffix }.Uri)
            .ToArray();
    }

}
