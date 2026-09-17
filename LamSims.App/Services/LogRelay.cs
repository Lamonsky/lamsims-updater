using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using LamSims.Core.Logging;

namespace LamSims.App.Services;

/// <param name="Time">Pre-rendered "HH:mm:ss". A string rather than a DateTimeOffset because the
/// item template binds ONE member per path and must not run a converter per line.</param>
/// <param name="IsWarning">Bound by `Classes.log-warning`, and a boolean per severity rather than
/// the enum because MainWindowBindingTests resolves each binding path as a single member.</param>
public sealed record LogEntry(string Time, string? Code, string Text, bool IsWarning, bool IsError);

/// <summary>
/// The App's ILogSink: it stamps the line and gets it off whatever thread produced it — the two
/// things Core cannot do. Split from LogViewModel because callers replace the dispatcher after
/// Build returns, so a sink that captured one at Build time would hold the discarded one. This
/// half is built early with none; the view model attaches later with the real one.
/// </summary>
public sealed class LogRelay(IClock clock) : ILogSink
{
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private int _wakePosted;

    /// <summary>Raised when lines become available and no wake is outstanding. May be attached
    /// after lines were written, so <see cref="Write"/> takes the flag only once a handler is
    /// present. A throwing handler is swallowed and the flag released: an
    /// <see cref="ILogSink"/> must never break the caller that logged through it.</summary>
    public Action? OnAvailable { get; set; }

    public void Write(LogLine line)
    {
        var local = clock.UtcNow.ToLocalTime();

        _pending.Enqueue(new LogEntry(
            local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            line.Code,
            line.Text,
            line.Severity == LogSeverity.Warning,
            line.Severity == LogSeverity.Error));

        TryWake();
    }

    public bool TryDequeue([MaybeNullWhen(false)] out LogEntry entry) => _pending.TryDequeue(out entry);

    /// <summary>Called by the consumer after draining. A write can arrive while the flag is still
    /// held, leaving its line queued with nobody watching, so this clears the flag and re-checks
    /// rather than depending on a later, unrelated write to surface it.</summary>
    public void ReleaseWake()
    {
        Interlocked.Exchange(ref _wakePosted, 0);
        if (!_pending.IsEmpty) TryWake();
    }

    /// <summary>Takes the wake flag and raises <see cref="OnAvailable"/>, at most once per
    /// outstanding wake. Reads the handler into a local first and does nothing when null, so a
    /// write landing before a consumer attaches never takes the flag and stalls forever.</summary>
    private void TryWake()
    {
        var handler = OnAvailable;
        if (handler is null) return;
        if (Interlocked.Exchange(ref _wakePosted, 1) != 0) return;

        try
        {
            handler();
        }
        catch
        {
            // ILogSink must never throw back into its caller — a ChunkFetcher retry loop, say —
            // and a dispatcher can throw while the app is shutting down. Release the flag so a
            // later write still gets a chance once a live consumer is attached.
            Interlocked.Exchange(ref _wakePosted, 0);
        }
    }
}
