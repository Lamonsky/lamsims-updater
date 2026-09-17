using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http.Headers;

namespace LamSims.Core.Downloading;

public enum RangeSupport { Supported, NotSupported }

public sealed record ProbeResult(RangeSupport RangeSupport, long TotalSize, MirrorValidator Validator);

/// <summary>
/// Establishes whether a mirror honours byte ranges, how large the entity is, and what
/// validator it offers. A 206 status is the authoritative signal, since Accept-Ranges is
/// advisory and commonly absent from servers that honour ranges anyway.
/// </summary>
public sealed class RangeProbe
{
    private readonly HttpClient _client;
    private readonly TimeSpan _headerTimeout;

    public RangeProbe(HttpClient client, TimeSpan? headerTimeout = null)
    {
        _client = client;
        _headerTimeout = headerTimeout ?? HttpDeadline.DefaultHeaderTimeout;
    }

    public async Task<ProbeResult> ProbeAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await HttpDeadline.SendAsync(_client, request, _headerTimeout, ct);
        response.EnsureSuccessStatusCode();

        var validator = new MirrorValidator(
            url.ToString(),
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToString("R"),
            response.Content.Headers.ContentLength);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            // Content-Length here is 1, the single probed byte. The entity size is the
            // length component of Content-Range.
            var totalSize = response.Content.Headers.ContentRange?.Length
                ?? throw new HttpRequestException($"'{url}' answered 206 without a Content-Range length.");

            return new ProbeResult(RangeSupport.Supported, totalSize, validator with { ContentLength = totalSize });
        }

        // A 200 carrying the whole body means ranges are unsupported. The response is
        // abandoned rather than read; the caller falls back to a single stream.
        var fullLength = response.Content.Headers.ContentLength
            ?? throw new HttpRequestException($"'{url}' answered 200 without a Content-Length.");

        return new ProbeResult(RangeSupport.NotSupported, fullLength, validator);
    }
}
