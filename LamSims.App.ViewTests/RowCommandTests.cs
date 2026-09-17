using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.Linq;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace LamSims.App.ViewTests;

/// <summary>
/// The row's context menu, invoked rather than read. Every existing check on these three items is
/// by NAME: the binding sweep resolves <c>{Binding XCommand}</c> against the type governing that
/// point in the tree, and its context-menu case asserts only that each of the three names appears
/// somewhere among the menu's descendants. Nothing pairs a Header with the Command beside it, so
/// swapping two commands that both exist on <c>PackRowViewModel</c> leaves every other test green
/// while "Cancel" removes and "Remove" cancels.
/// </summary>
public class RowCommandTests
{
    /// <summary>
    /// Opens the menu before reading it. A ContextMenu's items are popup content: until it opens
    /// they are not in the tree, their bindings have never been applied, and every Command reads
    /// null, which is also why no rendering test could catch a swapped pair by inspection.
    /// </summary>
    private static MenuItem Item(ViewHost host, string header)
    {
        var owner = ViewHost.Find<StackPanel>(host.RowVisual("EP01"), p => p.ContextMenu is not null);
        var menu = owner.ContextMenu!;

        if (!menu.IsOpen)
        {
            menu.Open(owner);
            host.Pump();
        }

        return menu.Items.OfType<MenuItem>()
            .First(i => string.Equals(i.Header?.ToString(), header, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Cancel_and_remove_each_reach_the_queue_method_their_header_names()
    {
        using var host = ViewHost.Show(Packs.Entry());

        // Queued is the one state in which both are offered, so neither is skipped for being
        // disabled; a test that invoked a disabled command would prove nothing about wiring.
        host.Row("EP01").ApplyQueue(new QueueItemSnapshot(
            "EP01", "Get to Work", QueueItemState.Queued, 0, 0, 0, null, null, null, []));
        host.Pump();

        Assert.True(host.Row("EP01").CanCancel);
        Assert.True(host.Row("EP01").CanRemove);

        Item(host, "Cancel").Command!.Execute(null);

        // The code matters as much as the method: a row bound to its neighbour's data context
        // would call the right method for the wrong pack.
        Assert.Equal(["Cancel:EP01"], host.Queue.Calls);

        Item(host, "Remove").Command!.Execute(null);

        Assert.Equal(["Cancel:EP01", "Remove:EP01"], host.Queue.Calls);
    }

    [AvaloniaFact]
    public void Reinstall_anyway_re_enables_the_checkbox_and_asks_the_queue_for_nothing()
    {
        using var host = ViewHost.Show(Packs.Entry());

        host.Row("EP01").ApplyScan(new PackScanResult("EP01", PackInstallState.Installed, [], null, null));
        host.Pump();

        Assert.False(host.Row("EP01").IsCheckable);

        Item(host, "Reinstall anyway").Command!.Execute(null);

        // Choosing to reinstall and choosing to reinstall now are two decisions, so this clears
        // the disable without checking the box or enqueueing anything, and never touches the
        // queue.
        Assert.True(host.Row("EP01").IsCheckable);
        Assert.False(host.Row("EP01").IsChecked);
        Assert.Empty(host.Queue.Calls);
    }
}
