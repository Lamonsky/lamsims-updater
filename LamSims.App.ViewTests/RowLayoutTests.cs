using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LamSims.App.ViewTests;

public class RowLayoutTests
{
    /// <summary>
    /// SizeText was computed and unit-tested but bound nowhere, so the size column did not exist
    /// in the shipped application.
    /// </summary>
    [AvaloniaFact]
    public void A_row_shows_the_pack_size()
    {
        using var host = ViewHost.Show(Packs.Entry("EP03", "City Living", 12_400_000_000));

        var row = host.RowVisual("EP03");
        var expected = host.Row("EP03").SizeText;

        Assert.Equal("12.4 GB", expected);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == expected);
    }

    [AvaloniaFact]
    public void The_size_column_is_the_secondary_colour()
    {
        using var host = ViewHost.Show(Packs.Entry("EP03", "City Living", 12_400_000_000));

        var size = ViewHost.Find<TextBlock>(host.RowVisual("EP03"), t => t.Text == "12.4 GB");

        Assert.Equal(Color.Parse("#a6adc8"),
            Assert.IsAssignableFrom<ISolidColorBrush>(size.Foreground).Color);
    }

    /// <summary>
    /// Covers IsCheckable, the binding and the rendered control together. An unscanned row's box
    /// must still be enabled, or "disabled" would be indistinguishable from "every box is
    /// disabled".
    /// </summary>
    [AvaloniaFact]
    public void An_installed_rows_checkbox_is_disabled_and_an_unscanned_one_is_not()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        host.Row("EP01").ApplyScan(new PackScanResult("EP01", PackInstallState.Installed, [], null, null));

        Assert.False(host.Row("EP01").IsCheckable);
        Assert.True(host.Row("EP02").IsCheckable);

        Assert.False(ViewHost.Find<CheckBox>(host.RowVisual("EP01")).IsEnabled);
        Assert.True(ViewHost.Find<CheckBox>(host.RowVisual("EP02")).IsEnabled);
    }

    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, "Get to Work", state, 42, 100, 0, null, null, null, Array.Empty<string>());

    private static TextBlock Status(ViewHost host, string code) =>
        ViewHost.Find<TextBlock>(host.RowVisual(code),
            t => t.Text == host.Row(code).StatusText);

    private static Color ForegroundOf(TextBlock text) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;

    /// <summary>
    /// The status carried no colour at all until now: only the message line below it did, so a
    /// failed row and an installed row read identically at a glance.
    /// </summary>
    [AvaloniaFact]
    public void A_rows_status_is_coloured_by_the_state_it_reports()
    {
        using var host = ViewHost.Show(
            Packs.Entry("EP01"), Packs.Entry("EP02"), Packs.Entry("EP03"),
            Packs.Entry("EP04"), Packs.Entry("EP05"));

        host.Row("EP01").ApplyScan(new PackScanResult("EP01", PackInstallState.NotInstalled, [], null, null));
        host.Row("EP02").ApplyScan(new PackScanResult("EP02", PackInstallState.Installed, [], null, null));
        host.Row("EP03").ApplyScan(new PackScanResult("EP03", PackInstallState.Partial, ["EP03"], null, null));
        host.Row("EP04").ApplyQueue(Snap("EP04", QueueItemState.Downloading));
        host.Row("EP05").ApplyQueue(Snap("EP05", QueueItemState.Failed));
        host.Pump();

        var wanted = new Dictionary<string, string>
        {
            ["EP01"] = "#a6adc8",  // Subtext0: idle keeps the resting secondary colour
            ["EP02"] = "#a6e3a1",  // Green
            ["EP03"] = "#f9e2af",  // Yellow
            ["EP04"] = "#89b4fa",  // Blue
            ["EP05"] = "#f38ba8",  // Red
        };

        foreach (var (code, hex) in wanted)
        {
            Assert.Equal(Color.Parse(hex), ForegroundOf(Status(host, code)));
        }
    }

    /// <summary>
    /// The bar was 120px wide and fixed, which made it the widest thing in the row and set the
    /// floor for the whole list column. Its percentage is already spelled out in StatusText.
    /// </summary>
    [AvaloniaFact]
    public void The_progress_bar_is_a_hairline_across_the_whole_row()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        host.Row("EP01").ApplyQueue(Snap("EP01", QueueItemState.Downloading));
        host.Pump();

        var row = host.RowVisual("EP01");
        var bar = ViewHost.Find<ProgressBar>(row);
        var line1 = ViewHost.Find<Grid>(row, g => g.RowDefinitions.Count == 2);

        Assert.True(bar.IsVisible);
        Assert.Equal(2d, bar.Bounds.Height);
        Assert.True(bar.Bounds.Width >= line1.Bounds.Width,
            $"the bar is {bar.Bounds.Width} wide in a row {line1.Bounds.Width} wide");
    }

    /// <summary>
    /// Fluent's CheckBox template pins the grid holding the box to Height="32" — a literal, not a
    /// resource, so no setter and no palette key can reach it — around a box that is 20 and
    /// centred. Left alone it made the first line of every pack row 32px tall to show 17px of
    /// text, and the box read as floating rather than sitting beside the name.
    /// </summary>
    [AvaloniaFact]
    public void The_checkbox_costs_the_first_line_no_dead_height()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01", "The Sims 4 Get to Work"));
        host.Pump();

        var row = host.RowVisual("EP01");
        var outer = ViewHost.Find<Grid>(row, g => g.RowDefinitions.Count == 2);
        var check = ViewHost.Find<CheckBox>(row);
        var box = ViewHost.Find<Border>(check, b => b.Name == "NormalRectangle");
        var name = ViewHost.Find<TextBlock>(row, t => t.Text == "The Sims 4 Get to Work");

        // The floor is still there — this is what the negative margin is paying for.
        Assert.Equal(32d, check.Bounds.Height);
        Assert.Equal(20d, box.Bounds.Height);

        Assert.Equal(box.Bounds.Height, outer.RowDefinitions[0].ActualHeight);

        double Middle(Visual v) =>
            v.TranslatePoint(new Point(0, v.Bounds.Height / 2), host.Window)!.Value.Y;

        Assert.True(Math.Abs(Middle(box) - Middle(name)) <= 1,
            $"the box centres at y={Middle(box)} and the name at y={Middle(name)}");
    }
}
