using System;
using System.Collections.Generic;
using System.Linq;
using LamSims.Core.Logging;

namespace LamSims.Core.Tests;

/// <summary>
/// Locked rather than a ConcurrentQueue: the tests assert ORDER as well as contents, and a
/// queue's enumeration order is only a snapshot.
/// </summary>
public sealed class RecordingLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly List<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    public IReadOnlyList<string> Texts => Lines.Select(l => l.Text).ToArray();

    public void Write(LogLine line)
    {
        lock (_gate) _lines.Add(line);
    }

    public bool Logged(string fragment) =>
        Lines.Any(l => l.Text.Contains(fragment, StringComparison.Ordinal));

    public bool Logged(string fragment, LogSeverity severity) =>
        Lines.Any(l => l.Severity == severity && l.Text.Contains(fragment, StringComparison.Ordinal));
}
