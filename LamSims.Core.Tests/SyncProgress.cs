using System;

namespace LamSims.Core.Tests;

/// <summary>
/// Invokes the handler on the reporting thread. Progress&lt;T&gt; posts asynchronously, which
/// makes "cancel on first report" and "the last report is the final one" races.
/// </summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
