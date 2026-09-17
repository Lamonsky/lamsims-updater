using LamSims.Core.Queueing;
using System.Linq;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LamSims.App.ViewTests;

public class RowMessageTests
{
    private static QueueItemSnapshot Failed(string code, string error) =>
        new(code, code, QueueItemState.Failed, 0, 0, 0, null, null, error, []);

    private static QueueItemSnapshot CompletedWithWarning(string code, string warning) =>
        new(code, code, QueueItemState.Completed, 0, 0, 0, null, null, null, [warning]);

    private static Color ColourOf(Control root, string text) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(
            ViewHost.Find<TextBlock>(root, t => t.Text == text).Foreground).Color;

    /// <summary>
    /// Asserts distinctness rather than membership: "the error is red" would pass
    /// in an application where every line was red.
    /// </summary>
    [AvaloniaFact]
    public void A_warning_an_error_and_ordinary_text_are_three_different_colours()
    {
        using var host = ViewHost.Show(
            Packs.Entry("SP01", "Luxury Party"),
            Packs.Entry("EP04", "Cats & Dogs"),
            Packs.Entry("EP01", "Get to Work"));

        host.Row("SP01").ApplyQueue(Failed("SP01", "the archive did not hash to the digest"));
        host.Row("EP04").ApplyQueue(CompletedWithWarning("EP04", "the archive was moved aside"));

        var error = ColourOf(host.RowVisual("SP01"), "the archive did not hash to the digest");
        var warning = ColourOf(host.RowVisual("EP04"), "the archive was moved aside");
        var ordinary = ColourOf(host.RowVisual("EP01"), "Get to Work");

        Assert.NotEqual(error, warning);
        Assert.NotEqual(error, ordinary);
        Assert.NotEqual(warning, ordinary);

        Assert.Equal(Color.Parse("#f38ba8"), error);
        Assert.Equal(Color.Parse("#f9e2af"), warning);
    }

    [AvaloniaFact]
    public void A_row_with_no_message_shows_no_message_line()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        Assert.Null(host.Row("EP01").Message);

        var lines = host.RowVisual("EP01").GetVisualDescendants().OfType<TextBlock>().ToList();

        // Assert.All over an empty sequence passes, so the row having any text at all is part of
        // the claim rather than an assumption.
        Assert.NotEmpty(lines);

        // Every realized TextBlock in the row is either non-empty or collapsed: an always-present
        // blank line would push every row taller for nothing.
        Assert.All(lines, t => Assert.True(!string.IsNullOrEmpty(t.Text) || !t.IsVisible));
    }

    /// <summary>
    /// What only this test can see is the class being *removed* as well as
    /// added: it captures one TextBlock instance and re-inspects it, so a stale 'warning' left
    /// beside the new 'error' fails here and nowhere else.
    /// </summary>
    [AvaloniaFact]
    public void A_warning_that_becomes_an_error_is_repainted()
    {
        using var host = ViewHost.Show(Packs.Entry("EP04", "Cats & Dogs"));

        host.Row("EP04").ApplyQueue(CompletedWithWarning("EP04", "the archive was moved aside"));

        var line = ViewHost.Find<TextBlock>(
            host.RowVisual("EP04"), t => t.Text == "the archive was moved aside");

        Assert.Contains("warning", line.Classes);
        Assert.Equal(Color.Parse("#f9e2af"),
            Assert.IsAssignableFrom<ISolidColorBrush>(line.Foreground).Color);

        host.Row("EP04").ApplyQueue(Failed("EP04", "the download failed"));

        Assert.Contains("error", line.Classes);
        Assert.DoesNotContain("warning", line.Classes);
        Assert.Equal(Color.Parse("#f38ba8"),
            Assert.IsAssignableFrom<ISolidColorBrush>(line.Foreground).Color);
    }
}
