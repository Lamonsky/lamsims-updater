using System;
using System.Collections.Generic;
using LamSims.App.Services;
using LamSims.Core.Logging;
using LamSims.Core.Queueing;

namespace LamSims.App.ViewModels;

/// <summary>
/// The one category of line the App produces rather than Core. Upstream logged a line per read;
/// here a 1.7 GB pack over sixteen connections would be millions of them, and throttling in the
/// sink means paying for every one. The App already receives the same numbers on every queue
/// snapshot, at a rate the UI can stand, so the ticker lives here and Core never sees it.
/// </summary>
public sealed class ProgressTicker(IClock clock, ILogSink log)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public void Observe(QueueItemSnapshot snapshot)
    {
        if (snapshot.State != QueueItemState.Downloading || snapshot.TotalBytes <= 0) return;

        var now = clock.UtcNow;

        if (_last.TryGetValue(snapshot.Code, out var last) && now - last < Window) return;

        _last[snapshot.Code] = now;

        var percent = snapshot.BytesCompleted * 100d / snapshot.TotalBytes;
        var rate = snapshot.BytesPerSecond > 0 ? $"  {LogFormat.Bytes((long)snapshot.BytesPerSecond)}/s" : "";

        log.Write(LogLine.Info($"{percent:0}%{rate}", snapshot.Code));
    }
}
