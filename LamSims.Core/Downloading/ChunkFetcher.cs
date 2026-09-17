using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using LamSims.Core.Logging;

namespace LamSims.Core.Downloading;

/// <summary>Abstracts backoff so tests never sleep.</summary>
public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemDelayProvider : IDelayProvider
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

public sealed record RetryOptions(
    int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay, TimeSpan ReadTimeout,
    TimeSpan HeaderTimeout)
{
    public static RetryOptions Default { get; } = new(
        MaxAttempts: 5,
        BaseDelay: TimeSpan.FromMilliseconds(250),
        MaxDelay: TimeSpan.FromSeconds(10),
        HeaderTimeout: HttpDeadline.DefaultHeaderTimeout,
        // Bounds a single ReadAsync, not the whole transfer, so a mirror that sends its
        // headers and then stops sending bytes fails into the retry loop.
        ReadTimeout: TimeSpan.FromSeconds(30));
}

/// <summary>
/// Where a fetch reports the bytes it has read. Passed per call rather than injected: one
/// ChunkFetcher serves every worker of a download, so a constructor-held sink could not tell
/// which worker was reading.
/// </summary>
public interface IChunkProgress
{
    /// <summary>Bytes just read by this attempt, incrementally.</summary>
    void Advanced(int worker, long bytes);

    /// <summary>This attempt is being discarded; the bytes it reported are garbage.</summary>
    void Abandoned(int worker);
}

/// <summary>
/// Raised when a mirror answers a conditional range request with 200, meaning its entity
/// changed underneath the download.
/// </summary>
public sealed class ValidatorMismatchException : Exception
{
    public ValidatorMismatchException(string url)
        : base($"'{url}' answered a conditional range request with 200: the file changed on the server.")
        => Url = url;

    public string Url { get; }
}

/// <summary>
/// Fetches one chunk, retrying with exponential backoff and rotating mirrors between
/// attempts. Writes land positionally on a shared handle, so workers need no lock. One
/// instance serves a whole download, so a mirror set aside for changing its entity stays
/// set aside for the chunks that follow.
/// </summary>
public sealed class ChunkFetcher
{
    private const int BufferSize = 1024 * 1024;

    private readonly HttpClient _client;
    private readonly RetryOptions _retry;
    private readonly IDelayProvider _delay;
    private readonly ILogSink _log;

    /// <summary>Mirrors that answered a conditional range request with 200, by url.</summary>
    private readonly ConcurrentDictionary<string, byte> _setAside = new(StringComparer.Ordinal);

    public ChunkFetcher(HttpClient client, RetryOptions retry, IDelayProvider delay, ILogSink? log = null)
    {
        _client = client;
        _retry = retry;
        _delay = delay;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>
    /// The pack this fetcher's owning download is for, so a log line can be attributed to it.
    /// One instance already serves a whole download (see the class remarks), so this is set
    /// once, right after construction, rather than threaded through FetchAsync's parameters,
    /// which stay exactly what every caller already passes.
    /// </summary>
    internal string? Code { get; set; }

    /// <summary>Returns the mirror that served the chunk, for the resume sidecar.</summary>
    public async Task<MirrorSource> FetchAsync(
        Chunk chunk,
        IReadOnlyList<MirrorSource> mirrors,
        int preferredMirror,
        int worker,
        SafeFileHandle target,
        IChunkProgress? progress,
        CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < _retry.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var mirror = NextMirror(mirrors, preferredMirror + attempt);
            if (mirror is null) break;

            try
            {
                await FetchOnceAsync(chunk, mirror, worker, target, progress, ct);
                return mirror;
            }
            catch (ValidatorMismatchException e)
            {
                // The entity changed there, so retrying the same bytes on that mirror cannot
                // help, but the other mirrors can still serve them. A round-robin cluster
                // whose nodes derive a different ETag from identical bytes would otherwise make
                // the pack permanently unfetchable, and re-probing rederives the same unstable
                // validator every run. No backoff, because another mirror is ready now.
                progress?.Abandoned(worker);
                _setAside[mirror.Url.ToString()] = 0;
                lastError = e;
                _log.Write(LogLine.Warning($"{mirror.Url} failed ({e.Message}), rotating", Code));
            }
            catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                progress?.Abandoned(worker);
                lastError = e;
                _log.Write(LogLine.Warning($"{mirror.Url} failed ({e.Message}), rotating", Code));
                if (attempt == _retry.MaxAttempts - 1) break;
                _log.Write(LogLine.Info(
                    $"Retry {attempt + 1} of {_retry.MaxAttempts} for chunk {chunk.Index}", Code));
                await _delay.DelayAsync(BackoffFor(attempt), ct);
            }
        }

        throw lastError ?? new HttpRequestException($"Chunk {chunk.Index} could not be fetched.");
    }

    /// <summary>
    /// The first mirror at or after <paramref name="start"/> that has not been set aside, or
    /// null once every one of them has been.
    /// </summary>
    private MirrorSource? NextMirror(IReadOnlyList<MirrorSource> mirrors, int start)
    {
        for (var offset = 0; offset < mirrors.Count; offset++)
        {
            var mirror = mirrors[(start + offset) % mirrors.Count];
            if (!_setAside.ContainsKey(mirror.Url.ToString())) return mirror;
        }

        return null;
    }

    private async Task FetchOnceAsync(
        Chunk chunk, MirrorSource mirror, int worker, SafeFileHandle target, IChunkProgress? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, mirror.Url);
        request.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.EndInclusive);

        // If-Range catches an entity changing mid-download rather than hours later at the
        // checksum. A mirror offering no usable validator omits it.
        if (mirror.Validator.IfRangeValue is { } ifRange)
            request.Headers.TryAddWithoutValidation("If-Range", ifRange);

        using var response = await HttpDeadline.SendAsync(_client, request, _retry.HeaderTimeout, ct);

        if (response.StatusCode == HttpStatusCode.OK && mirror.Validator.HasValidator)
            throw new ValidatorMismatchException(mirror.Url.ToString());

        response.EnsureSuccessStatusCode();

        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException(
                $"'{mirror.Url}' answered {(int)response.StatusCode} instead of 206 for a range request.");

        // A 206 status does not prove the server served the range that was asked for. Some
        // intermediaries answer 206 while returning the whole entity; writing that body at
        // this chunk's offset would corrupt the archive and still be flagged complete, so the
        // damage would only surface at the final checksum and survive every resume after it.
        var servedRange = response.Content.Headers.ContentRange;
        if (servedRange?.From != chunk.Start || servedRange.To != chunk.EndInclusive)
            throw new HttpRequestException(
                $"'{mirror.Url}' answered 206 for bytes {chunk.Start}-{chunk.EndInclusive} " +
                $"with Content-Range '{servedRange?.ToString() ?? "(absent)"}'.");

        await using var body = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[BufferSize];
        var offset = chunk.Start;
        var remaining = chunk.Length;

        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);

            // Each read gets its own deadline, rearmed after every successful read. A stall
            // surfaces as IOException so the retry filter catches it; an
            // OperationCanceledException would escape every filter in the engine.
            int read;
            using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readDeadline.CancelAfter(_retry.ReadTimeout);
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(0, wanted), readDeadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException(
                        $"'{mirror.Url}' sent no data for {_retry.ReadTimeout.TotalSeconds:0}s " +
                        $"during chunk {chunk.Index}.");
                }
            }

            if (read == 0)
                throw new IOException($"Chunk {chunk.Index} ended {remaining} bytes early.");

            await RandomAccess.WriteAsync(target, buffer.AsMemory(0, read), offset, ct);
            progress?.Advanced(worker, read);
            offset += read;
            remaining -= read;
        }
    }

    private TimeSpan BackoffFor(int attempt)
    {
        var exponential = _retry.BaseDelay * Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble() * 0.25 + 1.0;   // 1.00x–1.25x
        var delay = TimeSpan.FromMilliseconds(exponential.TotalMilliseconds * jitter);
        return delay > _retry.MaxDelay ? _retry.MaxDelay : delay;
    }
}
