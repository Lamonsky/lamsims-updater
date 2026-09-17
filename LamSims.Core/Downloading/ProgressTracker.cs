using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;

namespace LamSims.Core.Downloading;

/// <summary>
/// Byte accounting with an exponential moving average of speed.
/// </summary>
public sealed class ProgressTracker
{
    private const double SmoothingFactor = 0.3;

    private readonly string _code;
    private readonly long _totalBytes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    /// <summary>How long a window must be before the speed is resampled: per-read reporting takes
    /// Snapshot() to once per megabyte per worker, and 0.3 smoothing over sub-millisecond windows
    /// is not a readout anybody can use.</summary>
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>Bytes read by attempts still in flight, per worker. Not pre-sized: the worker
    /// count is known only after the tracker is built.</summary>
    private readonly Dictionary<int, long> _provisional = new();

    /// <summary>Test seam: real elapsed time in a unit test never reaches the 200ms window, so the
    /// window and the clamp are reachable only through a supplied clock.</summary>
    internal Func<TimeSpan> Clock { get; init; } = null!;

    private long _bytesCompleted;
    private long _lastBytes;
    private long _lastAccepted = -1;
    private TimeSpan _lastElapsed = TimeSpan.Zero;
    private double _bytesPerSecond;
    private DownloadProgress? _pending;
    private bool _reporting;

    /// <summary>Test seam, called between taking a pending snapshot and reporting it, so a test
    /// can deposit a newer one in the window where the reporter must pick it up. It runs inside the
    /// catch that swallows a throwing consumer, so assert after Deliver returns, not here.</summary>
    internal Action? BeforeReport;

    public ProgressTracker(string code, long totalBytes, long alreadyCompletedBytes = 0)
    {
        _code = code;
        _totalBytes = totalBytes;
        _bytesCompleted = alreadyCompletedBytes;
        _lastBytes = alreadyCompletedBytes;
        Clock = () => _clock.Elapsed;
    }

    /// <summary>
    /// Bytes on disk plus the bytes attempts in flight have reported. Delegates to
    /// <see cref="Snapshot"/> rather than repeating its summation: the two are the same quantity,
    /// and as two copies of the arithmetic they could disagree about provisional bytes while every
    /// test that reads only one of them stayed green.
    /// </summary>
    public long BytesCompleted => Snapshot().BytesCompleted;

    /// <summary>Bytes just read by an attempt that has not finished its chunk.</summary>
    public void Advance(int worker, long bytes)
    {
        lock (_gate)
        {
            _provisional.TryGetValue(worker, out var held);
            _provisional[worker] = held + bytes;
        }
    }

    /// <summary>The attempt was discarded; the bytes it read are garbage.</summary>
    public void Abandon(int worker)
    {
        lock (_gate) _provisional[worker] = 0;
    }

    /// <summary>A chunk finished. The commit and the clearing of that worker's provisional bytes
    /// take ONE lock acquisition: split, a snapshot between them counts the chunk twice and pushes
    /// BytesCompleted past the total — and since Deliver latches monotonically, the true final
    /// snapshot is then dropped and the caller stays above 100% for good.</summary>
    public void CommitChunk(int worker, long bytes)
    {
        lock (_gate)
        {
            _bytesCompleted += bytes;
            _provisional[worker] = 0;
        }
    }

    public DownloadProgress Snapshot()
    {
        lock (_gate)
        {
            var observed = _bytesCompleted;
            foreach (var held in _provisional.Values) observed += held;

            var elapsed = Clock();
            var window = elapsed - _lastElapsed;

            // Resampled only over a window long enough to mean something. Inside it the last
            // computed rate stands and _lastBytes/_lastElapsed are left alone, so the next real
            // sample measures the whole span rather than a sliver of it.
            if (window >= SpeedWindow)
            {
                // Clamped: an abandoned attempt hands bytes back, and a negative instant would
                // drag the average below zero and show the user a negative speed.
                var instant = Math.Max(0, (observed - _lastBytes) / window.TotalSeconds);
                _bytesPerSecond = _bytesPerSecond == 0
                    ? instant
                    : SmoothingFactor * instant + (1 - SmoothingFactor) * _bytesPerSecond;
                _lastBytes = observed;
                _lastElapsed = elapsed;
            }

            var remaining = _totalBytes - observed;
            TimeSpan? eta = _bytesPerSecond > 1 && remaining > 0
                ? TimeSpan.FromSeconds(remaining / _bytesPerSecond)
                : null;

            return new DownloadProgress(_code, observed, _totalBytes, _bytesPerSecond, eta);
        }
    }

    /// <summary>
    /// Hands a snapshot to the caller. At most one worker reports at a time, always the newest
    /// snapshot it can see; a worker arriving mid-report leaves its own behind and returns. Values
    /// stay non-decreasing and only the reporter blocks, so a slow handler cannot stall the
    /// transfer.
    ///
    /// Progress is a latest-value signal, so an overtaken snapshot is dropped rather than queued.
    /// Delivering the FINAL one depends on checking for a newer snapshot and clearing
    /// <c>_reporting</c> in ONE lock acquisition: split, a worker depositing between the two is
    /// neither picked up nor allowed to take over, and nothing re-drives delivery.
    ///
    /// A throwing handler is ignored: reporting must not fail a healthy transfer.
    /// </summary>
    public void Deliver(IProgress<DownloadProgress>? progress, DownloadProgress snapshot)
    {
        if (progress is null) return;

        lock (_gate)
        {
            if (snapshot.BytesCompleted <= _lastAccepted) return;
            _lastAccepted = snapshot.BytesCompleted;
            _pending = snapshot;

            if (_reporting) return;
            _reporting = true;
        }

        var stoodDown = false;

        try
        {
            while (true)
            {
                DownloadProgress due;

                lock (_gate)
                {
                    if (_pending is null)
                    {
                        _reporting = false;
                        stoodDown = true;
                        return;
                    }

                    due = _pending;
                    _pending = null;
                }

                try
                {
                    BeforeReport?.Invoke();
                    progress.Report(due);
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            // A flag left set would silently end delivery for the rest of the download, so it
            // is cleared even on a path nothing is expected to escape through.
            if (!stoodDown)
            {
                lock (_gate) _reporting = false;
            }
        }
    }
}
