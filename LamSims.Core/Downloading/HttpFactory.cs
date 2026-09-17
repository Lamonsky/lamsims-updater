using System;
using System.Net.Http;
using System.Threading;
using System.Net;

namespace LamSims.Core.Downloading;

/// <summary>
/// One shared, explicitly configured handler for every request the engine makes.
/// </summary>
public static class HttpFactory
{
    /// <summary>A client and the cookie container behind it. GoFile binds a download url to the
    /// token that listed it, and a handler's container cannot be read back off an HttpClient, so
    /// the resolver has to be handed the same instance the handler holds.</summary>
    public sealed record Http(HttpClient Client, CookieContainer Cookies);

    public static SocketsHttpHandler CreateHandler(
        int maxConnectionsPerServer, CookieContainer? cookies = null) => new()
    {
        // Off by default in SocketsHttpHandler. GoFile serves an HTML page under status 200 to a
        // request whose accountToken cookie is absent or stale, so for those hosts the cookie is
        // what separates the archive from a web page.
        CookieContainer = cookies ?? new CookieContainer(),
        UseCookies = true,
        // Load-bearing for throughput: an HTTP/2 host would otherwise multiplex every
        // range request onto a single TCP connection.
        EnableMultipleHttp2Connections = true,
        MaxConnectionsPerServer = maxConnectionsPerServer,
        // Archives are already compressed, and transparent decompression would break
        // byte-range accounting.
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        // Bounds draining an abandoned response body so its connection can be pooled. Reads
        // are bounded separately, per read, by RetryOptions.ReadTimeout.
        ResponseDrainTimeout = TimeSpan.FromSeconds(10),
    };

    public static HttpClient Create(int maxConnectionsPerServer) =>
        new(CreateHandler(maxConnectionsPerServer), disposeHandler: true)
        {
            // Whole-operation timeouts do not suit multi-gigabyte transfers.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>
    /// One client for the whole application. MaxConnectionsPerServer must be at least
    /// DownloadOptions.Connections, or workers block in the connection pool and the download
    /// runs at a fraction of its configured parallelism. Connections is a live setting, so the
    /// pool is sized for the largest value it can ever hold rather than for whichever one was
    /// configured at startup. The engine still opens one connection per worker and spawns only
    /// Connections workers.
    /// </summary>
    public static HttpClient Create() => Create(DownloadOptions.MaxConnections);

    /// <summary>The application's client, paired with the container the resolver writes into.</summary>
    public static Http CreateWithCookies()
    {
        var cookies = new CookieContainer();

        var client = new HttpClient(
            CreateHandler(DownloadOptions.MaxConnections, cookies), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        return new Http(client, cookies);
    }
}
