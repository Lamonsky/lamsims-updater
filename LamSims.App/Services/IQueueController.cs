using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;

namespace LamSims.App.Services;

/// <summary>
/// Everything the application may ask of the pack queue. Nothing outside
/// <c>PackQueueController</c> holds a <see cref="PackQueue"/>, so a row can Cancel and Remove
/// without owning one.
///
/// <see cref="Complete"/> is here even though the application never calls it: shutdown's
/// ordering rule (CancelAll then DisposeAsync, never Complete) has no deterministic observable
/// against a real queue, since both orderings end in cancellation microseconds apart. Against a
/// recording implementation the call sequence is the observable, and Complete has to be on the
/// interface for a test to show the wrong sequence failing.
/// </summary>
public interface IQueueController
{
    IAsyncEnumerable<QueueUpdate> Updates { get; }

    Task RunAsync(CancellationToken ct);

    void Enqueue(PackEntry pack, string gameDirectory);

    void Cancel(string code);

    /// <summary>
    /// False when the queue refused: the item is terminal, or it is the active one. A caller
    /// must not report that to the user; the next update says what actually happened.
    /// </summary>
    bool Remove(string code);

    void CancelAll();

    void Complete();

    void Pause();

    void Resume();

    ValueTask DisposeAsync();
}
