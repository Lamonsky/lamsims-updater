using System.Linq;
using Xunit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace LamSims.App.ViewTests;

/// <summary>
/// The catalog box takes a local path or an http(s) URL. Its two halves fail independently: a
/// one-way binding leaves the view model holding the old source while the box shows the new one,
/// and a button wired to the wrong command loads nothing at all.
/// </summary>
public class CatalogSourceTests
{
    private const string Typed = "https://mirror.example.invalid/catalog.json";

    /// <summary>The placeholder tells the catalog box from the search box; nothing else does.</summary>
    private static TextBox CatalogBox(Visual root) =>
        ViewHost.Find<TextBox>(root, t => t.PlaceholderText is not null);

    /// <summary>
    /// The box holds what the user typed and this line holds what is actually loaded, and the two
    /// disagree from the moment an edit is made until Load is pressed. Unlabelled, the line was a
    /// grey path sitting under a text box holding a different path, with nothing to say which one
    /// the application was reading — a user asked what it meant.
    ///
    /// Both halves are asserted absent first: a label that shows while there is nothing to label
    /// is a worse row than no row.
    ///
    /// The caption repeats the word Catalog rather than reading "In use:" alone, which was the first
    /// wording: the row sits under the Catalog box and beside nothing else, so on its own the label
    /// left a reader to infer what was in use.
    /// </summary>
    [AvaloniaFact]
    public void The_loaded_source_is_labelled_and_the_label_keeps_it_company()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        host.ViewModel.CatalogDescription = "";
        host.Pump();

        Assert.DoesNotContain(Visible(host), t => t == "Catalog (in use):");

        host.ViewModel.CatalogDescription = "/home/someone/catalog.json";
        host.Pump();

        var shown = Visible(host);
        Assert.Contains("Catalog (in use):", shown);
        Assert.Contains("/home/someone/catalog.json", shown);
    }

    private static string[] Visible(ViewHost host) =>
        host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible)
            .Select(t => t.Text ?? "")
            .ToArray();

    [AvaloniaFact]
    public void Typing_a_source_into_the_box_reaches_the_view_model()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        CatalogBox(host.Window).Text = Typed;
        host.Pump();

        Assert.Equal(Typed, host.ViewModel.CatalogInput);
    }

    [AvaloniaFact]
    public void Load_is_the_button_that_applies_what_was_typed()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var load = ViewHost.Find<Button>(host.Window, b => (b.Content as string) == "Load");

        Assert.Same(host.ViewModel.ApplyCatalogSourceCommand, load.Command);
    }
}
