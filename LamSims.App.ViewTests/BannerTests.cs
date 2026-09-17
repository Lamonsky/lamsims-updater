using System.Linq;
using Xunit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using LamSims.App.ViewModels;

namespace LamSims.App.ViewTests;

public class BannerTests
{
    private static Border BorderFor(ViewHost host, Banner banner) =>
        host.Window.GetVisualDescendants().OfType<Border>()
            .First(b => ReferenceEquals(b.DataContext, banner));

    /// <summary>
    /// BannerKind has to reach the border it colours, or a settings-load
    /// warning and a dead-queue error are the same grey box.
    /// </summary>
    [AvaloniaFact]
    public void The_three_banner_kinds_are_three_different_borders()
    {
        using var host = ViewHost.Show(Packs.Entry());

        var info = new Banner("a", "a catalog was loaded", BannerKind.Info);
        var warning = new Banner("b", "your settings could not be read", BannerKind.Warning);
        var error = new Banner("c", "the download queue stopped", BannerKind.Error);

        foreach (var banner in (Banner[])[info, warning, error]) host.ViewModel.Banners.Add(banner);

        // Banners are added after Show(), so their templates are not materialized until a layout
        // pass runs. The shipped app's message loop does this automatically; headless does not.
        host.Pump();

        var colours = new[] { info, warning, error }
            .Select(b => Assert.IsAssignableFrom<ISolidColorBrush>(BorderFor(host, b).BorderBrush).Color)
            .ToArray();

        Assert.Equal(3, colours.Distinct().Count());
        Assert.Equal(Color.Parse("#f9e2af"), colours[1]);
        Assert.Equal(Color.Parse("#f38ba8"), colours[2]);
    }

    /// <summary>
    /// Dismiss is the only route a user has to clear a banner, so it has to stay reachable. A
    /// horizontal StackPanel measures its children with infinite available width, which makes
    /// HorizontalAlignment="Stretch" inert and TextWrapping="Wrap" never fire: the text lays out
    /// on one unbroken line and the button is arranged past the window edge, with no horizontal
    /// scroll to reach it. Only a real two-column Grid keeps it inside.
    /// </summary>
    [AvaloniaFact]
    public void A_long_banner_keeps_its_dismiss_button_inside_the_window()
    {
        using var host = ViewHost.Show(Packs.Entry());

        // Banner text interpolates game-directory paths and exception messages, so this length is
        // ordinary rather than adversarial.
        var banner = new Banner("a", new string('x', 300), BannerKind.Error);
        host.ViewModel.Banners.Add(banner);
        host.Pump();

        var dismiss = host.Window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.DataContext, banner));

        // Bounds are parent-relative, so they have to be translated before they mean anything
        // about the window.
        var right = dismiss.TranslatePoint(new Point(dismiss.Bounds.Width, 0), host.Window);

        Assert.NotNull(right);
        Assert.True(dismiss.Bounds.Width > 0, "the Dismiss button was never arranged");
        Assert.True(right!.Value.X <= host.Window.Bounds.Width,
            $"Dismiss ends at x={right.Value.X} in a window {host.Window.Bounds.Width} wide");
    }
}
