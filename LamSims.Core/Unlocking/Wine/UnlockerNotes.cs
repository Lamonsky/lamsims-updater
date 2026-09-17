using System;

namespace LamSims.Core.Unlocking.Wine;

/// <summary>
/// One relay, created before both the Wine backend and the view model and handed to each. The
/// backend reports through it from the moment detection runs, which is before the view model has
/// anywhere to put the messages, so the sink is connected afterwards rather than injected.
/// </summary>
public sealed class UnlockerNotes : IProgress<string>
{
    private Action<string>? _sink;

    public void Connect(Action<string> sink) => _sink = sink;

    /// <summary>
    /// Dropped when nothing is connected. A diagnostic is never worth failing an operation for, and
    /// detection runs during startup before the window exists.
    /// </summary>
    public void Report(string value) => _sink?.Invoke(value);
}
