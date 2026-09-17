using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;

namespace LamSims.App.Services;

/// <summary>The only thing in the application that holds a <see cref="PackQueue"/>.</summary>
public sealed class PackQueueController(PackQueue queue) : IQueueController
{
    public IAsyncEnumerable<QueueUpdate> Updates => queue.Updates;

    public Task RunAsync(CancellationToken ct) => queue.RunAsync(ct);

    public void Enqueue(PackEntry pack, string gameDirectory) => queue.Enqueue(pack, gameDirectory);

    public void Cancel(string code) => queue.Cancel(code);

    public bool Remove(string code) => queue.Remove(code);

    public void CancelAll() => queue.CancelAll();

    public void Complete() => queue.Complete();

    public void Pause() => queue.Pause();

    public void Resume() => queue.Resume();

    public ValueTask DisposeAsync() => queue.DisposeAsync();
}
