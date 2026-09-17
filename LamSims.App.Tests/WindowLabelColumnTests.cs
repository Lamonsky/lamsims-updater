using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace LamSims.App.Tests;

/// <summary>
/// The settings rows each put their caption in a fixed-width first column, and those widths have to
/// agree or the captions stop forming one column down the window. Five grids carry it — game folder,
/// catalog, and the three inside Advanced — and nothing but this connects them.
///
/// The width itself cannot be checked here: proving a caption fits needs the real font manager, and
/// the headless one the view tests run on stubs the text shaper and measures every glyph to the same
/// width, under which any string at all fits anything. The measurement that chose 128 was taken
/// under Skia and is recorded in MainWindow.axaml beside the first grid; this guards the agreement,
/// which is the part a later edit is likely to break.
/// </summary>
public class WindowLabelColumnTests
{
    /// <summary>
    /// Every ColumnDefinitions whose FIRST column is a fixed number. A label grid is exactly that
    /// shape, and no other grid in the window has a numeric first column — the list/log split reads
    /// "*,8,2*" and every other row starts with Auto or a star.
    /// </summary>
    private static List<(string Value, double First)> LabelColumns() =>
        XamlSource.Window.Descendants()
            .Where(e => e.Name.LocalName == "Grid")
            .Select(e => e.Attribute("ColumnDefinitions")?.Value ?? "")
            .Where(value => value.Length > 0)
            .Select(value => (Value: value, First: value.Split(',')[0].Trim()))
            .Where(pair => double.TryParse(pair.First, NumberStyles.Float,
                                          CultureInfo.InvariantCulture, out _))
            .Select(pair => (pair.Value, double.Parse(pair.First, CultureInfo.InvariantCulture)))
            .ToList();

    [Fact]
    public void Every_settings_row_gives_its_label_the_same_column_width()
    {
        var columns = LabelColumns();

        // Not decoration: the filter above finding nothing would make the agreement below true of an
        // empty set, and the window really does have five of these.
        Assert.True(columns.Count >= 4,
            $"only {columns.Count} label grids were found, so this check has stopped seeing them");

        var widths = columns.Select(c => c.First).Distinct().ToList();

        Assert.True(widths.Count == 1,
            "the settings labels no longer share one column width, so they do not line up: "
            + string.Join(" / ", columns.Select(c => $"'{c.Value}' starts at {c.First}")));
    }
}
