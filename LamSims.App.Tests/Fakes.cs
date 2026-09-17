using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using LamSims.App.Services;

namespace LamSims.App.Tests;

/// <summary>
/// Records every call in order. The recorded sequence is what the shutdown test asserts.
/// </summary>
/// <param name="calls">
/// An externally owned log, so a test can interleave the queue's calls with something else's in
/// one ordered list. Shutdown's ordering rule has no other observable: a flush recorded in its
/// own list stays green however late it runs.
/// </param>
public sealed class RecordingQueue(List<string>? calls = null) : IQueueController
{
    private readonly Channel<QueueUpdate> _updates = Channel.CreateUnbounded<QueueUpdate>();

    public List<string> Calls { get; } = calls ?? new();

    public List<(string Code, string GameDirectory)> Enqueued { get; } = new();

    public bool RemoveResult { get; set; } = true;

    public IAsyncEnumerable<QueueUpdate> Updates => _updates.Reader.ReadAllAsync();

    public void Publish(QueueUpdate update) => _updates.Writer.TryWrite(update);

    /// <summary>
    /// The managed thread RunAsync was entered on. Zero until it is. Recorded because the queue
    /// must not run on the thread that started it: MainWindow.OnOpened awaits StartAsync on
    /// Avalonia's UI thread, and nothing in LamSims.Core or LamSims.App calls ConfigureAwait.
    /// </summary>
    public int RunThreadId { get; private set; }

    public Task RunAsync(CancellationToken ct)
    {
        RunThreadId = Environment.CurrentManagedThreadId;
        Add("Run");
        return Task.CompletedTask;
    }

    /// <summary>
    /// RunAsync now arrives from the thread pool rather than the caller's thread, so the list has
    /// more than one writer.
    /// </summary>
    private void Add(string call)
    {
        lock (Calls) Calls.Add(call);
    }

    public void Enqueue(PackEntry pack, string gameDirectory)
    {
        Add($"Enqueue:{pack.Code}");
        Enqueued.Add((pack.Code, gameDirectory));
    }

    public void Cancel(string code) => Add($"Cancel:{code}");

    public bool Remove(string code)
    {
        Add($"Remove:{code}");
        return RemoveResult;
    }

    /// <summary>
    /// Set to reproduce PackQueue.CancelAll's one throwing path: it ends in cts.Cancel(), which
    /// runs registered cancellation callbacks on the calling thread and rethrows them wrapped.
    /// </summary>
    public Exception? CancelAllThrows { get; set; }

    public void CancelAll()
    {
        Add("CancelAll");

        if (CancelAllThrows is { } failure) throw failure;
    }

    public void Complete() => Add("Complete");

    public void Pause() => Add("Pause");

    public void Resume() => Add("Resume");

    public ValueTask DisposeAsync()
    {
        Add("Dispose");
        _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public static class Packs
{
    public static PackEntry Entry(string code = "EP01", string name = "Get to Work", long size = 12_400_000_000) =>
        new(code, name, PackType.Expansion, size, null, new string('a', 64),
            [new Uri("https://example.invalid/" + code + ".zip")],
            [code]);
}
