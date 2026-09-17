using LamSims.Core.Logging;
using LamSims.Core.Queueing;
using Xunit;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace LamSims.App.ViewTests;

public class ThemeTests
{
    /// <summary>
    /// FluentTheme's own ButtonBackground is not Catppuccin Surface0, so this
    /// can only pass when Palette.axaml is merged into Application.Resources and wins the lookup.
    /// </summary>
    [AvaloniaFact]
    public void The_palette_overrides_fluents_own_button_background()
    {
        using var host = ViewHost.Show(Packs.Entry());

        var button = ViewHost.Find<Button>(host.Window);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(button.Background);

        Assert.Equal(Color.Parse("#313244"), brush.Color);
    }

    /// <summary>
    /// The theme's ground, which the window's own controls never exercise. Palette.axaml overrides
    /// no ComboBox key, so a ComboBox can only be themed through the palette handed to FluentTheme,
    /// and that is what was broken: 29 System*Color keys sat in Application.Resources where they
    /// resolved but where Fluent's own SystemControl*Brush definitions, which reference them with
    /// StaticResource inside the theme's dictionary scope, could not see them. Measured then:
    /// Background #66000000 behind White text.
    /// </summary>
    [AvaloniaFact]
    public void A_control_with_no_per_control_override_takes_the_palette_ground()
    {
        var combo = new ComboBox();
        var window = new Window { Width = 400, Height = 300, Content = combo };

        window.Show();

        // The pair. Foreground alone would also read #cdd6f4 from an inherited TextBlock brush;
        // the background can only come from the ground.
        Assert.Equal(Color.Parse("#cdd6f4"),
            Assert.IsAssignableFrom<ISolidColorBrush>(combo.Foreground!).Color);
        Assert.Equal(Color.Parse("#181825"),
            Assert.IsAssignableFrom<ISolidColorBrush>(combo.Background!).Color);
    }

    [AvaloniaFact]
    public void A_disabled_button_is_dimmed()
    {
        using var host = ViewHost.Show(Packs.Entry());

        var button = ViewHost.Find<Button>(host.Window);
        Assert.Equal(1d, button.Opacity);

        button.IsEnabled = false;

        Assert.Equal(0.5d, button.Opacity);
    }

    /// <summary>
    /// Reads the ContentPresenter, not the ListBoxItem. Fluent's ListBoxItem control theme paints
    /// its selected and pointer-over backgrounds onto PART_ContentPresenter through its own
    /// pseudo-class styles, which outrank the TemplateBinding carrying the item's Background down.
    /// The earlier form of this test asserted ListBoxItem.Background and passed while the presenter
    /// painted Fluent's #0078d7; it read the property that was SET rather than the one that PAINTS.
    ///
    /// Pointer-over gets no companion test. Driving :pointerover headlessly means synthesising a
    /// pointer at a position the item occupies, and this host does not lay out or hit-test to a
    /// point; a test that toggled the pseudo-class by hand would assert Avalonia's styling engine
    /// rather than that the pointer ever reaches the row. The selector is the same shape as the
    /// selected one and is covered by SelectorSweepTests' name and pseudo-class check only.
    /// </summary>
    [AvaloniaFact]
    public void A_selected_row_takes_the_selection_surface()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        var listBox = ViewHost.Find<ListBox>(host.Window);
        listBox.SelectedIndex = 0;

        var selected = SelectionSurface(host.RowVisual("EP01"));
        var other = SelectionSurface(host.RowVisual("EP02"));

        // The pair matters: asserting only that the selected row is Surface1 would pass if every
        // row were Surface1, which is not a selection indicator at all.
        Assert.Equal(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(selected.Background!).Color);
        Assert.NotEqual(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(other.Background!).Color);
    }

    /// <summary>
    /// The row's own template presenter. Matching on the name alone is not enough: the CheckBox
    /// nested in the row template has a PART_ContentPresenter of its own, so the templated parent
    /// is what disambiguates.
    /// </summary>
    private static ContentPresenter SelectionSurface(ListBoxItem row) =>
        ViewHost.Find<ContentPresenter>(
            row, p => p.Name == "PART_ContentPresenter" && ReferenceEquals(p.TemplatedParent, row));

    [AvaloniaFact]
    public void The_progress_indicator_is_mauve()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        host.Row("EP01").ApplyQueue(new QueueItemSnapshot(
            "EP01", "Get to Work", QueueItemState.Downloading,
            42, 100, 0, null, null, null, []));

        var bar = ViewHost.Find<ProgressBar>(host.RowVisual("EP01"));

        Assert.True(bar.IsVisible);
        Assert.Equal(Color.Parse("#cba6f7"),
            Assert.IsAssignableFrom<ISolidColorBrush>(bar.Foreground).Color);
    }

    /// <summary>
    /// Fluent resolves the slider's track fill and thumb from its accent, which is Windows blue.
    /// The two Slider* keys this phase originally shipped were both the container backdrop, which
    /// is Transparent and changes nothing a user sees.
    /// </summary>
    [AvaloniaFact]
    public void The_connections_slider_is_mauve_rather_than_fluents_accent()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        // The Slider is inside the collapsed Advanced Expander, which realizes no content until
        // it is opened; without this the search below finds nothing.
        ViewHost.Find<Expander>(host.Window).IsExpanded = true;
        host.Pump();

        var slider = ViewHost.Find<Slider>(host.Window);
        var thumb = ViewHost.Find<Thumb>(slider);
        var filled = ViewHost.Find<RepeatButton>(slider, b => b.Name == "PART_DecreaseButton");
        var rest = ViewHost.Find<RepeatButton>(slider, b => b.Name == "PART_IncreaseButton");

        Assert.Equal(Color.Parse("#cba6f7"),
            Assert.IsAssignableFrom<ISolidColorBrush>(thumb.Background!).Color);
        Assert.Equal(Color.Parse("#cba6f7"),
            Assert.IsAssignableFrom<ISolidColorBrush>(filled.Background!).Color);

        // The pair matters: a track painted mauve end to end indicates no value at all, and would
        // satisfy a lone "the track is mauve" assertion.
        Assert.Equal(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(rest.Background!).Color);
    }

    /// <summary>
    /// The checked mark is GreenBrush #a6e3a1: upstream stroked both the tick and the checked
    /// border. Without this the tick is Fluent's own and GreenBrush is a colour the application
    /// defines and never uses.
    ///
    /// Only the tick, deliberately. This test used to assert the green also reached
    /// PART_Border.BorderBrush, which it does — onto a border whose thickness Fluent's template
    /// pins to 0. Nothing was ever drawn in it, so the assertion held while the shipped window
    /// showed no green ring at all. What is checked now is what a user can see.
    /// </summary>
    [AvaloniaFact]
    public void A_checked_box_is_marked_green()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        host.Row("EP01").IsChecked = true;

        var ticked = ViewHost.Find<CheckBox>(host.RowVisual("EP01"));
        var blank = ViewHost.Find<CheckBox>(host.RowVisual("EP02"));

        Assert.True(ticked.IsChecked);
        Assert.False(blank.IsChecked);

        var glyph = ViewHost.Find<Avalonia.Controls.Shapes.Path>(ticked, p => p.Name == "CheckGlyph");

        Assert.Equal(Color.Parse("#a6e3a1"),
            Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Fill!).Color);

        // The pair: a box filled the same in both states marks nothing.
        Assert.Equal(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(Box(ticked).Background!).Color);
        Assert.Equal(Colors.Transparent,
            Assert.IsAssignableFrom<ISolidColorBrush>(Box(blank).Background!).Color);
    }

    /// <summary>
    /// PART_Border is the whole control — 20 wide by 32 tall around a 20 by 20 box — so anything
    /// painted into it overhangs the box by 6px top and bottom. The row's first line is 20px tall
    /// and the box fills it exactly; a filled PART_Border spilled over both neighbours.
    /// </summary>
    [AvaloniaFact]
    public void A_checkbox_paints_nothing_outside_its_box()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        host.Row("EP01").IsChecked = true;

        foreach (var check in new[] { ViewHost.Find<CheckBox>(host.RowVisual("EP01")),
                                      ViewHost.Find<CheckBox>(host.RowVisual("EP02")) })
        {
            var border = ViewHost.Find<Border>(check, b => b.Name == "PART_Border");
            var box = Box(check);

            Assert.Equal(32d, border.Bounds.Height);
            Assert.Equal(20d, box.Bounds.Height);

            Assert.Equal(Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(border.Background!).Color);

            // Fluent pins this to 0, which is why the eight CheckBoxBorderBrush* keys this
            // application used to define were all dead: a brush in a border 0px thick.
            Assert.Equal(default, border.BorderThickness);
        }
    }

    /// <summary>
    /// Program.BuildAvaloniaApp calls WithInterFont(), which registers the bundled face under the
    /// key "fonts:Inter" — and nothing named it, so every window ran in whatever face the platform
    /// defaults to. The bare family name "Inter" does not reach a registered collection: it
    /// resolves, silently, to the default, which is the same trap the log's "monospace" fell into.
    /// </summary>
    [AvaloniaFact]
    public void The_window_renders_in_the_bundled_face_and_the_log_keeps_its_own()
    {
        using var host = ViewHost.Show(out var services);
        host.ViewModel.BuildRows([Packs.Entry("EP01", "Get to Work")]);
        services.Log.Write(LogLine.Info("a log line"));
        host.Pump();

        // The family by value, because "it resolves to Inter" does not discriminate: with the
        // setter deleted the window falls back to the headless default, which resolves to the only
        // registered collection — this suite's own Inter — and every other assertion here still
        // holds. Measured, not assumed: deleting the setter passed this test until this line.
        Assert.Equal("fonts:Inter#Inter", host.Window.FontFamily.ToString());

        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(new Typeface(host.Window.FontFamily), out var face),
            $"the window asks for {host.Window.FontFamily}, which resolves to no typeface at all");
        Assert.Equal("Inter", face.FamilyName);

        // Inheritance carries it, so no control needs its own setter.
        var name = ViewHost.Find<TextBlock>(host.RowVisual("EP01"), t => t.Text == "Get to Work");

        Assert.Equal(host.Window.FontFamily, name.FontFamily);
        Assert.Equal(host.Window.FontFamily, ViewHost.Find<Button>(host.Window).FontFamily);

        // Except the log, whose own family has to survive a font set on the window above it.
        var line = ViewHost.Find<TextBlock>(host.Window, t => t.Classes.Contains("log-line"));

        Assert.NotEqual(host.Window.FontFamily, line.FontFamily);
    }

    private static Border Box(CheckBox check) =>
        ViewHost.Find<Border>(check, b => b.Name == "NormalRectangle");
}
