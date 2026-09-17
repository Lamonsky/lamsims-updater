using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace LamSims.App.ViewTests;

public class SmokeTests
{
    [AvaloniaFact]
    public void The_real_window_shows_headless_with_a_real_view_model()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        Assert.True(host.Window.IsVisible);

        // Rows are realized from the item template, not merely present in the collection: every
        // later task asserts against realized controls, and this is where that is established.
        Assert.NotNull(host.RowVisual("EP01"));
        Assert.NotNull(host.RowVisual("EP02"));

        Assert.NotSame(host.RowVisual("EP01"), host.RowVisual("EP02"));
    }
}
