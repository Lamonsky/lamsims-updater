using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Downloading;

namespace LamSims.App.Tests;

/// <summary>Records requested backoff without ever sleeping, so retry tests stay fast.</summary>
public sealed class FakeDelayProvider : IDelayProvider
{
    private readonly List<TimeSpan> _delays = new();

    public IReadOnlyList<TimeSpan> Delays
    {
        get { lock (_delays) return _delays.ToArray(); }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        lock (_delays) _delays.Add(delay);
        return Task.CompletedTask;
    }
}
