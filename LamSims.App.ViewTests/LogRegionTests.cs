using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using LamSims.Core.Logging;

namespace LamSims.App.ViewTests;

public class LogRegionTests
{
    /// <summary>
    /// TextBlock, the base type, because the log mixes both: the message column is a
    /// SelectableTextBlock and the time and code columns are plain.
    /// </summary>
    private static TextBlock Line(ViewHost host, string text) =>
        host.Window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private static SelectableTextBlock Selectable(ViewHost host, string text) =>
        host.Window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == text);

    private static Color Colour(TextBlock line) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(line.Foreground!).Color;

    /// <summary>
    /// The Copy button takes the whole log, which is no use to someone who wants one URL out of it.
    /// SelectableTextBlock is what makes a line selectable at all, so this fails against the plain
    /// TextBlock the log shipped with — not by reading a type name, but by selecting text and
    /// asking what came back.
    /// </summary>
    [AvaloniaFact]
    public void A_single_log_line_can_be_selected_on_its_own()
    {
        using var host = ViewHost.Show(out var services);

        services.Log.Write(LogLine.Info("Fetching EP01.zip", "EP01"));
        host.Pump();

        var line = Selectable(host, "Fetching EP01.zip");
        line.SelectAll();

        Assert.Equal("Fetching EP01.zip", line.SelectedText);

        // Surface2, from the palette's own TextControlSelectionHighlightColor, which Fluent's
        // SelectableTextBlock reads for its selection fill. No style in Controls.axaml sets this:
        // one was written and then deleted after this assertion passed with it gone. Asserted all
        // the same, because the alternative is Fluent's accent blue and nothing else would notice.
        Assert.Equal(Color.Parse("#585b70"),
            Assert.IsAssignableFrom<ISolidColorBrush>(line.SelectionBrush!).Color);
    }

    /// <summary>
    /// Avalonia's bare type selector is an EXACT type match, so "TextBlock.log-warning" reaches no
    /// SelectableTextBlock. Nothing about that failure is visible to the static sweeps — the classes
    /// are still applied, so the coverage checks in both directions stay green — and nothing about
    /// it throws. The log would simply render every severity in the inherited body colour. These are
    /// the four palette values the ":is(TextBlock)" form in Controls.axaml exists to deliver.
    /// </summary>
    [AvaloniaFact]
    public void Every_log_column_keeps_the_colour_its_class_gives_it()
    {
        using var host = ViewHost.Show(out var services);

        services.Log.Write(LogLine.Info("plain", "EP01"));
        services.Log.Write(LogLine.Warning("careful", "EP02"));
        services.Log.Write(LogLine.Error("broken", "EP03"));
        host.Pump();

        Assert.Equal(Color.Parse("#f9e2af"), Colour(Line(host, "careful")));
        Assert.Equal(Color.Parse("#f38ba8"), Colour(Line(host, "broken")));

        // An Info line earns no severity class and keeps the inherited body colour, which is what
        // makes the two above mean something: all three the same colour is the failure.
        Assert.Equal(Color.Parse("#cdd6f4"), Colour(Line(host, "plain")));

        // The code column, a plain TextBlock carrying `secondary`. Asserted here so that making it
        // selectable later without widening that selector too fails rather than silently recolours
        // it — the same trap one column over.
        Assert.Equal(Color.Parse("#a6adc8"), Colour(Line(host, "EP02")));
    }

    [AvaloniaFact]
    public void Each_written_line_renders_its_time_and_its_text()
    {
        using var host = ViewHost.Show(out var services);

        services.Log.Write(LogLine.Info("Fetching https://host.example.invalid/EP01.zip", "EP01"));
        host.Pump();

        var texts = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).ToList();

        Assert.Contains(texts, t => t is not null && t.Contains("Fetching https://host.example.invalid"));
        Assert.Contains("EP01", texts);

        // The TIME, which the name claims and nothing asserted: the Time TextBlock could be deleted
        // or repointed at another property and every test in both suites stayed green, leaving the
        // shipped log with no timestamps - the field someone copying the log to report a bug needs
        // most. Derived from the host's fixed clock rather than written as a literal, and rendered
        // in local time because that is what the view formats.
        var written = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero).ToLocalTime();

        Assert.Contains(written.ToString("HH:mm:ss", CultureInfo.InvariantCulture), texts);
    }

    /// <summary>
    /// The severity classes are the only thing that colours a line, and ThemeSweepTests only
    /// proves they are styled — not that they are ever put on an element at runtime.
    /// </summary>
    [AvaloniaFact]
    public void A_warning_line_carries_the_warning_class()
    {
        using var host = ViewHost.Show(out var services);

        services.Log.Write(LogLine.Warning("careful", "EP01"));
        host.Pump();

        Assert.Contains(host.Window.GetVisualDescendants().OfType<TextBlock>(),
                        t => t.Classes.Contains("log-warning"));
    }

    /// <summary>
    /// Each line's item template renders three "log-line" TextBlocks (Time, Code, Text), so a
    /// non-virtualizing StackPanel — the ItemsControl default when no ItemsPanel is set — would
    /// realize all 500 * 3 = 1500 of them regardless of what the window can actually show. A
    /// VirtualizingStackPanel instead realizes only what the viewport (plus a small overscan
    /// buffer) needs, which stays a small, roughly constant number as the line count grows. The
    /// threshold below sits far under 1500 specifically so this discriminates the two: it fails
    /// against the unvirtualized default and passes against VirtualizingStackPanel.
    /// </summary>
    [AvaloniaFact]
    public void Only_a_small_window_of_lines_is_realized_once_the_log_outgrows_the_viewport()
    {
        using var host = ViewHost.Show(out var services);

        for (var i = 0; i < 500; i++) services.Log.Write(LogLine.Info($"log line {i}"));
        host.Pump();

        var realized = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Count(t => t.Classes.Contains("log-line"));

        Assert.True(realized > 0, "no log lines were realized at all, so this proves nothing");
        Assert.True(realized < 600,
            $"expected far fewer than 1500 realized log TextBlocks with virtualization on, got {realized}");
    }
}
