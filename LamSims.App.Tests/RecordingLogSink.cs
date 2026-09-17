using System;
using System.Collections.Generic;
using System.Linq;
using LamSims.Core.Logging;

namespace LamSims.App.Tests;

/// <summary>
/// Minimal ILogSink double for App-side tests that only need to assert what was logged, not
/// exercise the real relay/dispatch machinery LogRelay provides.
/// </summary>
public sealed class RecordingLogSink : ILogSink
{
    private readonly List<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Lines => _lines;

    public void Write(LogLine line) => _lines.Add(line);

    public bool Logged(string fragment) =>
        _lines.Any(l => l.Text.Contains(fragment, StringComparison.Ordinal));
}
