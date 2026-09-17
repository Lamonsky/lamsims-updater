using System;
using System.Linq;
using System.Threading;
using Xunit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LamSims.App.ViewModels;
using LamSims.Core.Unlocking;

namespace LamSims.App.ViewTests;

public class WindowLayoutTests
{
    [AvaloniaFact]
    public void The_window_has_a_resize_floor()
    {
        using var host = ViewHost.Show(Packs.Entry());

        // Star columns with no floor collapse into illegibility; a window that has never been
        // given a minimum can be dragged to nothing.
        Assert.Equal(760d, host.Window.MinWidth);
        Assert.Equal(560d, host.Window.MinHeight);
    }

    /// <summary>
    /// The settings block: the StackPanel holding the game folder, the catalog box, both
    /// expanders, the banners and the Scan row. Found through the scroller docked to the top of
    /// the window's DockPanel, which bounds the region's share of the window.
    /// </summary>
    private static StackPanel SettingsRegion(ViewHost host) =>
        (StackPanel)ViewHost.Find<DockPanel>(host.Window).Children
            .OfType<ScrollViewer>().First().Content!;

    /// <summary>
    /// Fluent's Expander theme leaves HorizontalAlignment at Left, which makes an expander size to
    /// its content in both states: the Advanced section measured 145px closed and 389px open in an
    /// 876px column, so unfolding it moved its own right edge and the chevron with it. Both states
    /// are asserted against the region's width, because equal-to-each-other alone would also be
    /// true of two identically wrong widths.
    /// </summary>
    [AvaloniaFact]
    public void An_expander_is_the_width_of_its_column_open_or_closed()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var column = SettingsRegion(host).Bounds.Width;

        Assert.True(column > 0, "the settings region laid out no content to measure against");

        // Visible ones only: the DLC Unlocker section hides itself when no backend reports support,
        // which is the case for this host, and a hidden control arranges to zero width.
        var expanders = host.Window.GetVisualDescendants().OfType<Expander>()
            .Where(e => e.IsVisible).ToList();

        Assert.NotEmpty(expanders);

        foreach (var expander in expanders)
        {
            Assert.Equal(column, expander.Bounds.Width);
        }

        foreach (var expander in expanders) expander.IsExpanded = true;
        host.Pump();

        foreach (var expander in expanders)
        {
            Assert.Equal(column, expander.Bounds.Width);
        }
    }

    /// <summary>
    /// The two settings sections are mutually exclusive, and the reason is arithmetic rather than
    /// taste: the region is docked to the top of a DockPanel, so both open at the default window
    /// height left the pack list and the log arranged past the bottom edge at zero height, with no
    /// scrollbar anywhere — the scroll lives inside each growable list, not around the region.
    ///
    /// Both directions are driven, because a handler wired to one section only would pass a test
    /// that opened them in one order.
    /// </summary>
    [AvaloniaFact]
    public void Opening_one_settings_section_closes_the_other()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var advanced = ViewHost.Find<Expander>(host.Window, e => e.Name == "AdvancedSection");
        var unlocker = ViewHost.Find<Expander>(host.Window, e => e.Name == "UnlockerSection");

        advanced.IsExpanded = true;
        host.Pump();
        Assert.True(advanced.IsExpanded);
        Assert.False(unlocker.IsExpanded);

        unlocker.IsExpanded = true;
        host.Pump();
        Assert.False(advanced.IsExpanded);
        Assert.True(unlocker.IsExpanded);

        advanced.IsExpanded = true;
        host.Pump();
        Assert.True(advanced.IsExpanded);
        Assert.False(unlocker.IsExpanded);

        // The pack list and the log survive it, which is the point of the rule.
        AssertTheLogIsOnScreen(host);
    }

    /// <summary>
    /// The settings region is docked to the top, and a docked child is given its full desired
    /// height, so an unbounded list inside it arranges the pack list and the log past the bottom
    /// edge with no scrollbar anywhere to reach them. Each list that can grow therefore carries its
    /// OWN scroll — the banner run here, the unlocker's target list — rather than the region
    /// scrolling as a whole, which took the game folder and the catalog box out of reach with it.
    ///
    /// Banners crowd the region from a test more cheaply than anything else; the reported bug
    /// arrived through the unlocker's detection notes, which stacked in the same StackPanel.
    /// </summary>
    [AvaloniaFact]
    public void A_crowded_settings_region_cannot_push_the_pack_list_off_the_window()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        for (var i = 0; i < 30; i++)
        {
            host.ViewModel.Banners.Add(new Banner($"b{i}", $"banner {i}", BannerKind.Info));
        }

        host.Pump();

        // The rows above the banners must NOT have scrolled away, which is the whole reason the
        // scroll moved inside: the game folder label is the first thing in the region.
        var labels = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible).Select(t => t.Text).ToArray();
        Assert.Contains("Game folder:", labels);
        Assert.Contains("Catalog:", labels);

        AssertTheLogIsOnScreen(host);
    }

    /// <summary>
    /// Translated to the window, because Bounds is relative to the parent and the failure this
    /// guards is about WHERE the DockPanel's fill child was arranged, not how big it thinks it is.
    /// </summary>
    private static void AssertTheLogIsOnScreen(ViewHost host)
    {
        var log = ViewHost.Find<Border>(host.Window, b => b.Classes.Contains("log"));
        var origin = log.TranslatePoint(new Point(0, 0), host.Window);

        Assert.NotNull(origin);
        Assert.True(log.Bounds.Height > 100d,
            $"the log was arranged {log.Bounds.Height}px tall, so it is on screen in name only");
        Assert.True(origin.Value.Y + log.Bounds.Height <= host.Window.Bounds.Height,
            $"the log runs to {origin.Value.Y + log.Bounds.Height}px in a "
            + $"{host.Window.Bounds.Height}px window, so its bottom is off screen");
    }

    /// <summary>
    /// Covers REALIZED content only. Popup content (the row context menu) and anything inside a
    /// collapsed region is structurally invisible to a visual-tree walk, so this makes its claim
    /// about what the window has actually laid out, not about the markup as a whole.
    /// </summary>
    [AvaloniaFact]
    public void No_realized_text_in_the_window_is_pinned_to_a_fixed_width()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        // A collapsed Expander realizes no content, so without this the Advanced region (the
        // downloads and connections rows) is silently skipped and this test's universal claim
        // is false.
        ViewHost.Find<Expander>(host.Window).IsExpanded = true;
        host.Pump();

        // Control.Width is NaN unless explicitly set, so this reads the rendered objects rather
        // than the markup, and covers the row template as well as the chrome.
        var pinned = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => !double.IsNaN(t.Width))
            .Select(t => $"realized TextBlock '{t.Text}' is pinned to Width={t.Width}")
            .ToList();

        Assert.True(pinned.Count == 0, string.Join(Environment.NewLine, pinned));
    }

    [AvaloniaFact]
    public void The_row_columns_are_the_shape_the_spec_gives()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var outer = ViewHost.Find<Grid>(host.RowVisual("EP01"), g => g.RowDefinitions.Count == 2);

        Assert.Equal(
            ["Auto", "1*", "Auto"],
            outer.ColumnDefinitions.Select(c => c.Width.ToString()).ToArray());

        // The second line: code, then the status filling what is left. Only the star column can
        // wrap, which is why the status lives there and the code does not.
        //
        // A direct child, not a descendant search: Fluent's CheckBox template contains a Grid of
        // its own, and a two-column predicate matches that one first.
        var line2 = Assert.Single(outer.Children.OfType<Grid>());

        Assert.Equal(
            ["Auto", "1*"],
            line2.ColumnDefinitions.Select(c => c.Width.ToString()).ToArray());
    }

    /// <summary>
    /// The list is the column that gives way. Its rows trim and wrap; the log's lines are the
    /// text a user is reading while they wait, so the star share goes there.
    /// </summary>
    [AvaloniaFact]
    public void The_log_column_is_twice_the_pack_list()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var split = ViewHost.Find<Grid>(host.Window,
            g => g.ColumnDefinitions.Count == 3
                 && g.Children.OfType<Border>().Any(b => b.Classes.Contains("log")));

        Assert.Equal(
            ["1*", "8", "2*"],
            split.ColumnDefinitions.Select(c => c.Width.ToString()).ToArray());
    }

    [AvaloniaFact]
    public void Shutting_down_makes_the_content_inert_but_leaves_the_notice_readable()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var notice = ViewHost.Find<TextBlock>(host.Window,
            t => t.Text is not null && t.Text.StartsWith("Finishing up", StringComparison.Ordinal));

        Assert.False(notice.IsVisible);
        Assert.True(ViewHost.Find<Button>(host.Window).IsEffectivelyEnabled);

        host.ViewModel.IsShuttingDown = true;

        Assert.True(notice.IsVisible);
        Assert.True(notice.IsEffectivelyEnabled);
        Assert.False(ViewHost.Find<Button>(host.Window).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void The_empty_state_shows_only_when_there_is_something_to_say()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        // Located by name rather than by predicate. Now that the window is themed, a Button's
        // content presenter also renders a TextBlock inheriting MainViewModel as its DataContext,
        // and a centre-alignment predicate can match that instead, asserting the wrong control
        // while reading as a pass.
        var empty = host.Window.FindControl<TextBlock>("EmptyState")!;

        Assert.NotNull(empty);
        Assert.False(empty.IsVisible);

        host.ViewModel.EmptyStateMessage = "This catalog lists no packs.";

        Assert.True(empty.IsVisible);
        Assert.Equal("This catalog lists no packs.", empty.Text);

        // IsVisible says nothing about occlusion, and this test once passed while the label was
        // invisible to users: the ListBox carries an opaque background, a Panel draws in document
        // order, and nothing binds the list's IsVisible, so a label declared before the list is
        // painted and then covered. The assertion is about relative order for that reason.
        var panel = ViewHost.Find<Panel>(host.Window, p => p.Children.Contains(empty));
        var list = ViewHost.Find<ListBox>(host.Window);

        Assert.True(panel.Children.IndexOf(empty) > panel.Children.IndexOf(list),
            "the empty-state label must be declared after the ListBox or the list paints over it");
    }
    /// <summary>
    /// A supported backend with three targets, which is what a Linux machine with a couple of
    /// launchers reports. ViewHost registers no backend by default, so the DLC Unlocker section
    /// hides itself and every other test in this file measures a window that cannot show it - which
    /// is why the region overflowing with that section open went uncaught.
    /// </summary>
    private static ViewHost ShowWithUnlocker()
    {
        var backend = new RecordingUnlockerBackend
        {
            Targets = Enumerable.Range(0, 3)
                .Select(i => new UnlockerTarget(
                    "test-backend", ClientKind.EaApp, $"/clients/ea{i}", $"EA app {i}"))
                .ToArray(),
        };

        var host = ViewHost.Show([], new UnlockerService([backend]), new FakeUnlockerHost(),
            new StubUnlockerAssets());

        var refresh = host.ViewModel.Unlocker.RefreshAsync(CancellationToken.None);
        Assert.True(ViewHost.PumpUntil(() => refresh.IsCompleted), "detection never finished");

        return host;
    }

    /// <summary>
    /// The settings region is docked to the top of a DockPanel, so it takes its full desired
    /// height and the pack list and the log divide whatever is left. With the DLC Unlocker open
    /// the region wants 429px at every window size, which left the two of them 69px each at the
    /// declared 560 minimum and 149 at the 640 default - the log "on screen in name only" that
    /// AssertTheLogIsOnScreen exists to reject.
    ///
    /// The scroller around the region caps it at a share of the window instead, so the content
    /// below keeps a floor at every supported size.
    ///
    /// Driven at the MINIMUM rather than the default: the default is where the bug was reported,
    /// but the minimum is the worst case the window promises to support, and a fix measured only at
    /// 640 leaves 560 broken.
    /// </summary>
    [AvaloniaFact]
    public void The_unlocker_section_cannot_crowd_out_the_pack_list_at_the_minimum_size()
    {
        using var host = ShowWithUnlocker();

        host.Window.Width = host.Window.MinWidth;
        host.Window.Height = host.Window.MinHeight;
        ViewHost.Find<Expander>(host.Window, e => e.Name == "UnlockerSection").IsExpanded = true;
        host.Pump();

        AssertTheLogIsOnScreen(host);

        // The pack list too, not only the log: they share the fill row, and a fix that rescued one
        // by starving the other would pass an assertion naming just the log.
        var list = ViewHost.Find<ListBox>(host.Window);
        Assert.True(list.Bounds.Height > 100d,
            $"the pack list was arranged {list.Bounds.Height}px tall with the unlocker open");

        // The section is still reachable, which is what makes this a cap rather than a clip: its
        // own controls scroll within the region instead of pushing the window's content out.
        var scroller = ViewHost.Find<DockPanel>(host.Window).Children.OfType<ScrollViewer>().First();
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height,
            "the region fits its cap, so this window never exercised the scroll");
    }

    /// <summary>
    /// The cap is a share of the window, so it must stop binding once the window is tall enough to
    /// hold the region outright. A literal MaxHeight could not: below the region's 429px it would
    /// clip a section even where there is ample room.
    /// </summary>
    [AvaloniaFact]
    public void A_tall_window_shows_the_whole_settings_region_unscrolled()
    {
        using var host = ShowWithUnlocker();

        host.Window.Width = 900;
        host.Window.Height = 1000;
        ViewHost.Find<Expander>(host.Window, e => e.Name == "UnlockerSection").IsExpanded = true;
        host.Pump();

        var scroller = ViewHost.Find<DockPanel>(host.Window).Children.OfType<ScrollViewer>().First();

        Assert.True(scroller.Extent.Height <= scroller.Viewport.Height,
            $"the region wanted {scroller.Extent.Height}px inside a {scroller.Viewport.Height}px "
            + "viewport, so it scrolls in a window with room to show it whole");

        AssertTheLogIsOnScreen(host);
    }

}
