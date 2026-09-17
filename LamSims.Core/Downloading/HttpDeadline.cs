using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LamSims.Core.Downloading;

/// <summary>
/// Bounds the wait for response headers. HttpClient.Timeout is disabled for the engine, since
/// whole-operation timeouts do not suit multi-gigabyte transfers, and ConnectTimeout ends at
/// the handshake, so without this a mirror that accepts a connection and then says nothing
/// occupies a worker indefinitely.
/// </summary>
public static class HttpDeadline
{
    public static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends <paramref name="request"/> and requires its response headers within
    /// <paramref name="headerTimeout"/>. The deadline is reported as an IOException so it
    /// lands in the same retry filters as a mid-body stall; cancellation through
    /// <paramref name="ct"/> still propagates as an OperationCanceledException.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, TimeSpan headerTimeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(headerTimeout);

        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(
                $"'{request.RequestUri}' sent no response headers within " +
                $"{headerTimeout.TotalSeconds:0}s.");
        }
    }
}
