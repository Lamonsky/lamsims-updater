using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LamSims.App.ViewModels;
using LamSims.Core.Queueing;

namespace LamSims.App.Services;

/// <summary>
/// The only reader of the queue's updates. The channel is single-reader, so a second consumer
/// anywhere in the application will lose updates.
///
/// Every update carries every item, so one post per update replaces the whole view and a
/// coalesced or dropped post loses nothing.
/// </summary>
public sealed class QueueBridge(
    IQueueController queue,
    IUiDispatcher dispatcher,
    Func<string, PackRowViewModel?> rowFor,
    Func<IEnumerable<PackRowViewModel>> allRows,
    Action<QueueUpdate> onUpdate,
    Action<Exception?> onClosed)
{
    public async Task RunAsync()
    {
        Exception? fault = null;

        try
        {
            await foreach (var update in queue.Updates)
            {
                var current = update;
                dispatcher.Post(() => Apply(current));
            }
        }
        catch (Exception e)
        {
            fault = e;
        }

        dispatcher.Post(() => onClosed(fault));
    }

    private void Apply(QueueUpdate update)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in update.Items)
        {
            present.Add(item.Code);

            // Ordinal: PackQueue matches its own items ordinally, and both sides carry the code
            // from the same PackEntry, so these strings are identical rather than equivalent.
            //
            // A code with no row is impossible while the catalog cannot change under a live
            // queue. If it ever happens, the rule that guarantees it was broken elsewhere.
            rowFor(item.Code)?.ApplyQueue(item);
        }

        foreach (var row in allRows())
        {
            if (!present.Contains(row.Code)) row.ClearQueueOverlay();
        }

        // Last, deliberately: this is what the view model and the tests observe, so anything
        // done after it races its observer.
        onUpdate(update);
    }
}
