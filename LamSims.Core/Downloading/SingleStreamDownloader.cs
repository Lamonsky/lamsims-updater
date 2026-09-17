using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Logging;

namespace LamSims.Core.Downloading;

/// <summary>
/// The fallback for servers that ignore byte ranges. It offers no speedup and cannot resume,
/// so a retry restarts from zero, but it goes through the same partial file and the same
/// checksum gate, so a completed archive is trusted on the same terms. Each attempt takes the
/// next url in turn, so a mirror that dies mid-transfer costs one attempt rather than the
/// download.
/// </summary>
public sealed class SingleStreamDownloader
{
    private const int BufferSize = 1024 * 1024;

    private readonly HttpClient _client;
    private readonly DownloadPaths _paths;
    private readonly RetryOptions _retry;
    private readonly IDelayProvider _delay;
    private readonly ILogSink _log;

    public SingleStreamDownloader(
        HttpClient client, DownloadPaths paths, RetryOptions retry, IDelayProvider delay,
        ILogSink? log = null)
    {
        _client = client;
        _paths = paths;
        _retry = retry;
        _delay = delay;
        _log = log ?? NullLogSink.Instance;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request, IReadOnlyList<Uri> urls, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var state = new PartStateStore(_paths.StateFile(request.Code));

        Exception? lastError = null;
        var transferred = false;

        for (var attempt = 0; attempt < _retry.MaxAttempts && !transferred; attempt++)
        {
            var url = urls[attempt % urls.Count];

            try
            {
                // Backoff runs inside the try, so a cancellation during it is reported as
                // Cancelled rather than escaping uncaught to the caller.
                if (attempt > 0)
                    await _delay.DelayAsync(BackoffFor(attempt - 1), ct);

                _paths.EnsureCreated();

                await TransferAsync(request, url, state, progress, ct);
                transferred = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return DownloadResult.Cancelled(usedSingleStream: true);
            }
            catch (Exception e) when (e is HttpRequestException or IOException
                                           or UnauthorizedAccessException)
            {
                lastError = e;
            }
        }

        if (!transferred)
            return DownloadResult.Failed(
                lastError?.Message ?? $"{request.Code} could not be downloaded.", usedSingleStream: true);

        // Finalizing gets its own attempts rather than sharing the transfer's. Hashing and
        // renaming a file that is already complete fails for reasons of its own, usually a
        // scanner holding the just-closed file, and those are worth waiting out, where
        // re-entering the transfer loop would truncate the finished part file and pull every
        // byte down again.
        for (var attempt = 0; attempt < _retry.MaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 0)
                    await _delay.DelayAsync(BackoffFor(attempt - 1), ct);

                var result = await new ArchiveFinalizer(_paths)
                    .FinalizeAsync(request.Code, request.Sha256, state, usedSingleStream: true, ct);

                switch (result.Outcome)
                {
                    case DownloadOutcome.Completed:
                        _log.Write(LogLine.Info(
                            $"sha256 verified {result.ActualSha256![..8]}…", request.Code));
                        break;
                    case DownloadOutcome.ChecksumMismatch:
                        _log.Write(LogLine.Error(
                            $"Digest mismatch: expected {request.Sha256[..8]}…, got {result.ActualSha256![..8]}…",
                            request.Code));
                        break;
                }

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return DownloadResult.Cancelled(usedSingleStream: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lastError = e;
            }
        }

        return DownloadResult.Failed(
            lastError?.Message ?? $"{request.Code} could not be finalized.", usedSingleStream: true);
    }

    /// <summary>Same capped, jittered backoff the segmented path uses.</summary>
    private TimeSpan BackoffFor(int attempt)
    {
        var exponential = _retry.BaseDelay * Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble() * 0.25 + 1.0;   // 1.00x–1.25x
        var delay = TimeSpan.FromMilliseconds(exponential.TotalMilliseconds * jitter);
        return delay > _retry.MaxDelay ? _retry.MaxDelay : delay;
    }

    private async Task TransferAsync(
        DownloadRequest request, Uri url, PartStateStore state,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpDeadline.SendAsync(_client, message, _retry.HeaderTimeout, ct);
        response.EnsureSuccessStatusCode();

        var tracker = new ProgressTracker(request.Code, request.Size);

        await using var body = await response.Content.ReadAsStreamAsync(ct);

        // Resume state is discarded at the moment the partial file is overwritten, never
        // before, so an earlier failure leaves an existing segmented download's sidecar intact.
        state.Delete();

        await using var file = new FileStream(
            _paths.PartFile(request.Code), FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        var received = 0L;

        while (true)
        {
            // Each read gets its own deadline, rearmed after every successful read. A stall
            // surfaces as IOException so the retry filter catches it; an
            // OperationCanceledException would escape every filter in the engine.
            int read;
            using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readDeadline.CancelAfter(_retry.ReadTimeout);
                try
                {
                    read = await body.ReadAsync(buffer, readDeadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"'{url}' sent no data for {_retry.ReadTimeout.TotalSeconds:0}s.");
                }
            }

            if (read == 0) break;

            // The transfer is a separate request from the probe, so its length is unproven
            // here. Refusing to write past the expected size keeps a server that never stops
            // sending from filling the disk, one buffer being the most it can overshoot.
            received += read;
            if (received > request.Size)
                throw new IOException($"'{url}' sent more than the expected {request.Size} bytes.");

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            tracker.CommitChunk(0, read);
            tracker.Deliver(progress, tracker.Snapshot());
        }

        // A stream that ends early ends cleanly, so without this the short file would reach
        // the checksum and be quarantined, which the user then has to clear by hand, where a
        // truncated transfer belongs in the retry loop.
        if (received != request.Size)
            throw new IOException($"'{url}' ended after {received} of {request.Size} bytes.");

        // To the disk, not just to the OS: the finalizer hashes this file out of the page
        // cache and renames it, so a power loss straight afterwards would otherwise leave a
        // full-length archive of zeroed blocks. The segmented path flushes every chunk to disk
        // the same way.
        file.Flush(flushToDisk: true);
    }
}
