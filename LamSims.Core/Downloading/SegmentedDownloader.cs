using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;
using LamSims.Core.Logging;

namespace LamSims.Core.Downloading;

public sealed class SizeMismatchException : Exception
{
    public SizeMismatchException(long expectedSize, long actualSize)
        : base($"The catalog lists {expectedSize} bytes but the server reports {actualSize}.")
    {
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
    }

    public long ExpectedSize { get; }
    public long ActualSize { get; }
}

/// <summary>
/// Downloads one archive over several connections, resuming from a sidecar when one
/// exists, and gates completion on SHA-256. One archive downloads at a time; the
/// connection budget belongs to that archive.
/// </summary>
public sealed class SegmentedDownloader
{
    private readonly HttpClient _client;
    private readonly DownloadPaths _paths;
    private readonly DownloadOptions _options;
    private readonly RetryOptions _retry;
    private readonly IDelayProvider _delay;
    private readonly ILogSink _log;

    public SegmentedDownloader(
        HttpClient client, DownloadPaths paths, DownloadOptions options, RetryOptions retry,
        IDelayProvider delay, ILogSink? log = null)
    {
        _client = client;
        _paths = paths;
        _options = options;
        _retry = retry;
        _delay = delay;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>
    /// Reports the caller's cancellation through the result's <c>Cancelled</c> outcome
    /// rather than throwing <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_paths.Root))
            return DownloadResult.Failed("The download directory must not be blank.");

        try
        {
            // Inside the try: the pack code is untrusted enough to be rejected as a file name,
            // and this method always returns a DownloadResult.
            var state = new PartStateStore(_paths.StateFile(request.Code));

            _paths.EnsureCreated();

            // Only the bytes not already on disk. Charging for the whole archive would refuse a
            // nearly-finished resume. Before any request, so a hopeless download costs nothing.
            var partFile = _paths.PartFile(request.Code);
            var alreadyOnDisk = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;
            DiskSpace.EnsureAvailable(_paths.Root, request.Size - Math.Min(alreadyOnDisk, request.Size));

            var (mirrors, rangeLess) = await ProbeMirrorsAsync(request, ct);

            // The fallback runs only when no mirror honours ranges. It spins up no workers, so
            // there is no connection count — without a line here a range-less mirror shows
            // minutes of apparently dead log.
            if (mirrors.Count == 0)
            {
                LogFetchStart(request, connections: null);
                return await new SingleStreamDownloader(_client, _paths, _retry, _delay, _log)
                    .DownloadAsync(request, rangeLess, progress, ct);
            }

            var chunks = ChunkPlan.Create(request.Size, _options.ChunkSize);

            // Carried-over completions keep the mirror they were attributed to. The sidecar
            // is read once here and never re-read, so the two views cannot disagree.
            var carried = ResumableChunks(state, request, mirrors, chunks.Count);
            var resumable = carried.Select(c => c.Index).ToHashSet();

            var pending = chunks.Where(c => !resumable.Contains(c.Index)).ToList();
            var completedBytes = chunks.Where(c => resumable.Contains(c.Index)).Sum(c => c.Length);

            // The worker count, not chunks.Count: a resume leaves fewer pending, and the budget
            // caps at Connections. Computed once and threaded through, so the logged number and
            // the running count cannot drift.
            var workerCount = Math.Min(_options.Connections, Math.Max(pending.Count, 1));

            LogFetchStart(request, workerCount);

            if (carried.Count > 0)
            {
                _log.Write(LogLine.Info(
                    $"Resuming {carried.Count} of {chunks.Count} chunks from a previous run", request.Code));
            }

            var tracker = new ProgressTracker(request.Code, request.Size, completedBytes);
            Preallocate(partFile, request.Size);

            var done = carried.ToDictionary(c => c.Index);

            // One snapshot before any worker starts. Without it a resume whose chunks are all
            // trusted runs no workers and tells the caller nothing at all, and every download
            // shows 0% until its first chunk lands.
            tracker.Deliver(progress, tracker.Snapshot());

            using (var handle = File.OpenHandle(
                partFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous))
            {
                await RunWorkersAsync(
                    request, mirrors, pending, handle, state, done, tracker, progress, workerCount, ct);
            }

            var result = await new ArchiveFinalizer(_paths)
                .FinalizeAsync(request.Code, request.Sha256, state, usedSingleStream: false, ct);

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
            // The partial file and its sidecar are retained so the transfer resumes rather
            // than restarting. The filter keeps an OperationCanceledException the caller did
            // not ask for out of this path, where it would read as a pause.
            return DownloadResult.Cancelled();
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                       or UnauthorizedAccessException or ArgumentException
                                       or SizeMismatchException or ValidatorMismatchException)
        {
            // UnauthorizedAccessException does not derive from IOException, and every call here can
            // raise it. ArgumentException covers the path APIs, which reject a name rather than
            // failing to use it. This method always returns a DownloadResult.
            return DownloadResult.Failed(e.Message);
        }
    }

    private async Task<(IReadOnlyList<MirrorSource> Ranged, IReadOnlyList<Uri> RangeLess)> ProbeMirrorsAsync(
        DownloadRequest request, CancellationToken ct)
    {
        var probe = new RangeProbe(_client, _retry.HeaderTimeout);
        var ranged = new List<MirrorSource>();
        var rangeLess = new List<Uri>();
        Exception? lastError = null;

        foreach (var url in request.Urls)
        {
            try
            {
                var result = await probe.ProbeAsync(url, ct);

                if (result.TotalSize != request.Size)
                    throw new SizeMismatchException(request.Size, result.TotalSize);

                if (result.RangeSupport == RangeSupport.Supported)
                    ranged.Add(new MirrorSource(url, result.Validator));
                else
                    rangeLess.Add(url);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or SizeMismatchException)
            {
                // Per url, including the size disagreement raised just above: one stale mirror
                // must not stop the mirrors after it from being probed.
                lastError = e;
                _log.Write(LogLine.Warning($"{url} failed ({e.Message}), rotating", request.Code));
            }
        }

        if (ranged.Count == 0 && rangeLess.Count == 0)
            throw lastError ?? new HttpRequestException($"No mirror for {request.Code} could be reached.");

        return (ranged, rangeLess);
    }

    /// <summary>Which chunks may be trusted from a previous run, with the mirror each came from.
    /// An absent, unreadable or inconsistent sidecar yields none. A chunk survives only while its
    /// mirror is still present with the same validator, so a changed entity invalidates that
    /// mirror's work and nobody else's.</summary>
    private IReadOnlyList<CompletedChunk> ResumableChunks(
        PartStateStore store, DownloadRequest request, IReadOnlyList<MirrorSource> mirrors,
        int chunkCount)
    {
        // A sidecar whose partial file is absent or the wrong length describes chunks that are not
        // on disk, and preallocation would zero-fill them. Before Preallocate, which is what makes
        // the length comparison meaningful.
        var partFile = _paths.PartFile(request.Code);
        if (!File.Exists(partFile) || new FileInfo(partFile).Length != request.Size)
            return Array.Empty<CompletedChunk>();

        var saved = store.TryLoad();

        // A positional record leaves an omitted collection null, and a null ELEMENT is the same
        // hazard a step further in: no serializer writes one, but this file is untrusted.
        if (saved is null
            || saved.CompletedChunks is null
            || saved.Mirrors is null
            || saved.CompletedChunks.Any(c => c is null)
            || saved.Mirrors.Any(m => m is null)
            || saved.Code != request.Code
            || saved.TotalSize != request.Size
            || saved.ChunkSize != _options.ChunkSize
            || !string.Equals(saved.ExpectedSha256, request.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<CompletedChunk>();
        }

        // Untrusted input. An index outside the plan or repeated twice makes the set incoherent
        // and would throw rather than resume; treating it as absent costs a restart, never a
        // wrong result.
        var indices = saved.CompletedChunks.Select(c => c.Index).ToArray();
        if (indices.Any(i => i < 0 || i >= chunkCount) || indices.Distinct().Count() != indices.Length)
            return Array.Empty<CompletedChunk>();

        var trustedMirrors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mirror in mirrors)
        {
            var previous = saved.Mirrors.FirstOrDefault(m => m.Url == mirror.Url.ToString());
            if (previous is not null && previous.Matches(mirror.Validator))
                trustedMirrors.Add(mirror.Url.ToString());
        }

        return saved.CompletedChunks.Where(c => trustedMirrors.Contains(c.MirrorUrl)).ToArray();
    }

    /// <summary>The lines every path emits before transferring a byte.
    /// <paramref name="connections"/> is null on the range-less path, where no workers run, so the
    /// suffix is left off rather than printing a number describing nothing.</summary>
    private void LogFetchStart(DownloadRequest request, int? connections)
    {
        _log.Write(LogLine.Info($"Fetching {request.Urls[0]}", request.Code));

        var suffix = connections is { } n ? $" over {n} connections" : "";
        _log.Write(LogLine.Info($"{LogFormat.Bytes(request.Size)}{suffix}", request.Code));
    }

    private static void Preallocate(string path, long size)
    {
        using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        if (RandomAccess.GetLength(handle) != size)
            RandomAccess.SetLength(handle, size);
    }

    private async Task RunWorkersAsync(
        DownloadRequest request,
        IReadOnlyList<MirrorSource> mirrors,
        IReadOnlyList<Chunk> pending,
        SafeFileHandle handle,
        PartStateStore state,
        Dictionary<int, CompletedChunk> done,
        ProgressTracker tracker,
        IProgress<DownloadProgress>? progress,
        int workerCount,
        CancellationToken ct)
    {
        var queue = new ChunkQueue(pending);
        var fetcher = new ChunkFetcher(_client, _retry, _delay, _log) { Code = request.Code };
        var errors = new ConcurrentQueue<Exception>();
        var sink = new TrackerSink(tracker, progress);

        // Held across the mutation AND the sidecar write, so snapshots persist in build order:
        // saving outside the lock lets a stale snapshot overwrite a newer one.
        using var bookkeeping = new SemaphoreSlim(1, 1);

        // A failing worker cancels its siblings. Without this the remaining workers keep
        // transferring the rest of an archive whose download has already failed.
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
        {
            try
            {
                while (queue.TryTake(out var chunk))
                {
                    failure.Token.ThrowIfCancellationRequested();

                    var servedBy = await fetcher.FetchAsync(
                        chunk!, mirrors, worker % mirrors.Count, worker, handle, sink, failure.Token);

                    // Data must reach disk before the chunk is flagged complete: otherwise a
                    // power loss can leave a "done" chunk whose bytes never left the page
                    // cache, and resume would skip it.
                    RandomAccess.FlushToDisk(handle);

                    DownloadProgress snapshot;

                    await bookkeeping.WaitAsync(failure.Token);
                    try
                    {
                        done[chunk!.Index] = new CompletedChunk(chunk.Index, servedBy.Url.ToString());

                        await state.SaveAsync(
                            new PartState(
                                request.Code, request.Size, request.Sha256, _options.ChunkSize,
                                done.Values.OrderBy(c => c.Index).ToArray(),
                                mirrors.Select(m => m.Validator).ToArray()),
                            failure.Token);

                        tracker.CommitChunk(worker, chunk.Length);
                        snapshot = tracker.Snapshot();
                    }
                    finally
                    {
                        bookkeeping.Release();
                    }

                    // After the release: a blocking handler would otherwise hold the lock every
                    // other worker needs, and no token can interrupt it.
                    tracker.Deliver(progress, snapshot);
                }
            }
            catch (Exception e)
            {
                errors.Enqueue(e);
                if (!ct.IsCancellationRequested) failure.Cancel();
                throw;
            }
            // Task.Run takes no token: one already cancelled would create the task
            // pre-cancelled and never run the delegate. Cancellation is observed inside the
            // loop instead, where it reaches `errors`.
        })).ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch
        {
            // Task.WhenAll rethrows only the first exception; every worker's is in `errors`.
        }

        // Prefer a genuine failure over the cancellations it caused in its siblings.
        var primary = errors.FirstOrDefault(e => e is not OperationCanceledException)
            ?? errors.FirstOrDefault();
        if (primary is not null)
            ExceptionDispatchInfo.Capture(primary).Throw();

        // Backstop: a cancellation that somehow left no recorded exception must still surface
        // as one, or the caller would finalize a download that never finished.
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Routes per-read reports into the tracker and out to the caller, so a one-chunk pack shows
    /// movement. Fires once per ReadAsync RETURN — tens of kilobytes, not the full buffer.
    /// Deliver coalesces and holds no shared lock, so it cannot stall the other workers, though it
    /// does block the reporting worker's own read loop while it drains.
    /// </summary>
    private sealed class TrackerSink(ProgressTracker tracker, IProgress<DownloadProgress>? progress)
        : IChunkProgress
    {
        public void Advanced(int worker, long bytes)
        {
            tracker.Advance(worker, bytes);
            tracker.Deliver(progress, tracker.Snapshot());
        }

        public void Abandoned(int worker) => tracker.Abandon(worker);
    }
}
