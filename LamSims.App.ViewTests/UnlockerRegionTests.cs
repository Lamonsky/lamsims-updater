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

/// <summary>
/// The DLC Unlocker region. Every case below EXECUTES a command and reads what it reached, rather
/// than checking a Command's bound name, which is the only way to catch an
/// InstallSelectedCommand/RemoveSelectedCommand swap.
/// </summary>
public class UnlockerRegionTests
{
    private static UnlockerTarget Target(string path = "/clients/ea", string display = "EA app",
        ClientKind kind = ClientKind.EaApp) =>
        new("test-backend", kind, path, display);

    private static Expander UnlockerExpander(ViewHost host) =>
        ViewHost.Find<Expander>(host.Window, e => e.Header?.ToString() == "DLC Unlocker");

    /// <summary>
    /// Shows the window with a supported backend, runs RefreshAsync so <c>Targets</c> is populated,
    /// then expands the region and pumps.
    ///
    /// An Expander's content is realised LAZILY. Until <c>IsExpanded</c> is set, the content is not
    /// in the visual tree, its bindings have never been applied, and every Command inside reads
    /// null, the same trap RowCommandTests documents for ContextMenu, which needs
    /// <c>menu.Open(owner)</c> before its items can be read.
    ///
    /// RefreshAsync's own detection call hops onto Task.Run (it runs a real filesystem walk in
    /// production), so its continuation is marshalled back through the dispatcher's synchronization
    /// context — blocking on it here with GetAwaiter().GetResult() would deadlock, since nothing
    /// would be left to pump that context while this thread waits on it. PumpUntil drives the
    /// dispatcher instead, exactly as every operation below already does for InstallAsync/RemoveAsync.
    /// </summary>
    private static ViewHost ShowExpanded(out RecordingUnlockerBackend backend, params UnlockerTarget[] targets)
    {
        backend = new RecordingUnlockerBackend { Targets = targets };

        var host = ViewHost.Show([], new UnlockerService([backend]), new FakeUnlockerHost(),
            new StubUnlockerAssets());

        var refresh = host.ViewModel.Unlocker.RefreshAsync(CancellationToken.None);
        Assert.True(ViewHost.PumpUntil(() => refresh.IsCompleted), "detection never finished");

        UnlockerExpander(host).IsExpanded = true;
        host.Pump();

        return host;
    }

    private static UnlockerTarget[] ManyTargets(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new UnlockerTarget("test-backend", ClientKind.EaApp,
                $"/home/playday/Games/Heroic/Prefixes/default/Game{i}/drive_c/Program Files/"
                + "Electronic Arts/EA Desktop", "EA app"))
            .ToArray();

    private static double SectionHeight(ViewHost host) => UnlockerExpander(host).Bounds.Height;

    /// <summary>
    /// The target list scrolls inside the section, so the section's own height stops depending on
    /// how many clients were found. That is what keeps the pack list and the log on screen: this
    /// region is docked to the top of a DockPanel and takes its full desired height, so an
    /// unbounded list here arranges them past the bottom edge with no scrollbar to reach them.
    ///
    /// Two counts either side of the cap, compared to each other: asserting one height against a
    /// literal would pass against a list that grows and happens to match at that count.
    /// </summary>
    [AvaloniaFact]
    public void The_section_is_the_same_height_however_many_clients_were_found()
    {
        using var few = ShowExpanded(out _, ManyTargets(8));
        using var many = ShowExpanded(out _, ManyTargets(15));

        Assert.Equal(SectionHeight(few), SectionHeight(many));

        // And the buttons below the list are still inside the window, which is what the user could
        // not reach before the list was bounded.
        foreach (var host in new[] { few, many })
        {
            var install = host.Window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Content?.ToString() == "Install selected");
            var bottom = install.TranslatePoint(new Point(0, install.Bounds.Height), host.Window);

            Assert.NotNull(bottom);
            Assert.True(bottom.Value.Y <= host.Window.Bounds.Height,
                $"Install selected runs to {bottom.Value.Y}px in a {host.Window.Bounds.Height}px window");
        }
    }

    /// <summary>
    /// A detection note is the one thing in this section a user must read — a prefix they named that
    /// is not one, a sandbox, nothing found at all. It sits OUTSIDE the target list's scroller for
    /// that reason: inside it, a machine with eight clients scrolled the note out of sight behind
    /// rows nobody had to read, which is where the first version of this layout put it.
    /// </summary>
    [AvaloniaFact]
    public void A_detection_note_is_visible_behind_a_target_list_long_enough_to_scroll()
    {
        using var host = ShowExpanded(out _, ManyTargets(15));

        const string note = "No Wine prefix was found. Set its path in the Wine prefix setting.";
        host.ViewModel.Unlocker.DetectionNotes.Add(note);
        host.ViewModel.Unlocker.HasDetectionNotes = true;
        host.Pump();

        var block = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == note);

        Assert.NotNull(block);
        Assert.True(block.IsVisible, "the note is in the tree but not visible");

        // The CLIP, not IsVisible and not the arranged position. A note inside the scroller is
        // still visible and still reports a position — it is simply clipped away by the viewport,
        // which is exactly the bug, and both of the weaker assertions passed against it.
        var transformed = block.GetTransformedBounds();

        Assert.NotNull(transformed);

        // Bounds are in the element's own space and Clip is in the window's, so the transform has
        // to be applied before they can be compared — without it the two rectangles are in
        // different coordinate systems and the comparison is meaningless.
        var onScreen = transformed.Value.Bounds.TransformToAABB(transformed.Value.Transform);

        Assert.True(transformed.Value.Clip.Contains(onScreen),
            $"the note is on screen at {onScreen} but clipped to {transformed.Value.Clip}, "
            + "so part of it cannot be seen");
    }

    private static Grid ProgressRow(ViewHost host) =>
        ViewHost.Find<Grid>(host.Window, g => g.Children.OfType<ProgressBar>().Any());

    [AvaloniaFact]
    public void The_region_is_hidden_when_the_service_reports_no_support()
    {
        // The default ViewHost.Show() registers no backend, exactly as every pre-existing test
        // relies on, so IsSupported is false and the region must stay hidden.
        using var host = ViewHost.Show();

        Assert.False(UnlockerExpander(host).IsVisible);
    }

    // The Install button must reach the backend's InstallAsync with this target's own ClientPath.
    // Asserting only that some install ran would also be satisfied by a Command swapped with
    // Remove.
    [AvaloniaFact]
    public void Executing_the_install_button_reaches_the_backend_with_this_targets_path()
    {
        var target = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out var backend, target);
        host.ViewModel.Unlocker.Targets[0].IsSelected = true;

        ViewHost.Find<Button>(host.Window, b => b.Content as string == "Install selected").Command!.Execute(null);
        Assert.True(ViewHost.PumpUntil(() => !host.ViewModel.Unlocker.IsBusy),
            "the install never finished: " + string.Join(",", backend.Calls));

        Assert.Contains("Install:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Remove:/clients/origin", backend.Calls);
    }

    // ---- pair: same as above, for Remove. ----
    [AvaloniaFact]
    public void Executing_the_remove_button_reaches_the_backend_with_this_targets_path()
    {
        var target = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out var backend, target);
        host.ViewModel.Unlocker.Targets[0].IsSelected = true;

        ViewHost.Find<Button>(host.Window, b => b.Content as string == "Remove selected").Command!.Execute(null);
        Assert.True(ViewHost.PumpUntil(() => !host.ViewModel.Unlocker.IsBusy),
            "the remove never finished: " + string.Join(",", backend.Calls));

        Assert.Contains("Remove:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Install:/clients/origin", backend.Calls);
    }

    // Every other test in this file sets IsSelected/IsBusy on the view model directly and never
    // touches the checkbox itself, so nothing else would notice IsChecked="{Binding IsSelected}"
    // or IsEnabled="{Binding !IsBusy}" being deleted from the template. This drives the CONTROL,
    // both directions, so a deleted binding fails here instead of shipping silently.
    [AvaloniaFact]
    public void The_row_checkbox_binds_its_selection_and_its_enabled_state()
    {
        var target = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out _, target);
        var row = host.ViewModel.Unlocker.Targets[0];
        var box = host.Window.GetVisualDescendants().OfType<CheckBox>()
            .First(c => c.DataContext is UnlockerTargetViewModel r && r.ClientPath == target.ClientPath);

        box.IsChecked = true;
        host.Pump();
        Assert.True(row.IsSelected);

        row.IsBusy = true;
        host.Pump();
        Assert.False(box.IsEnabled);
    }

    /// <summary>
    /// Sets the view model's own progress fields directly rather than driving a real operation:
    /// UnlockerViewModelTests already proves RunBatchAsync populates them correctly from a
    /// backend, so this only needs to prove the XAML binds to them.
    /// </summary>
    [AvaloniaFact]
    public void The_progress_line_appears_while_busy_and_shows_the_current_step()
    {
        var target = Target();
        using var host = ShowExpanded(out _, target);
        var vm = host.ViewModel.Unlocker;

        Assert.False(ProgressRow(host).IsVisible);

        vm.IsBusy = true;
        vm.CurrentStep = "Stopping the client";
        host.Pump();

        var row = ProgressRow(host);
        Assert.True(row.IsVisible);
        Assert.Contains(row.Children.OfType<TextBlock>(), t => t.Text == "Stopping the client");
    }

    // ---- pair: Maximum must track Total, not just Value tracking Completed. Binding Value alone
    // would leave Maximum at its default of 100, the exact stuck-bar symptom the step-count
    // contract exists to prevent, arriving here by a different route. ----
    [AvaloniaFact]
    public void The_progress_bar_maximum_tracks_total_and_value_tracks_completed()
    {
        var target = Target();
        using var host = ShowExpanded(out _, target);
        var vm = host.ViewModel.Unlocker;

        vm.Total = 5;
        vm.Completed = 3;
        host.Pump();

        var bar = ViewHost.Find<ProgressBar>(host.Window);
        Assert.Equal(5, bar.Maximum);
        Assert.Equal(3, bar.Value);

        // The denominator must reach the UI too: Total already reaches the bar's fill through
        // Maximum, but the text beside it must not show the numerator alone, or the user sees "3"
        // where the design shows "3/5".
        var texts = ProgressRow(host).Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("3", texts);
        Assert.Contains("5", texts);
    }

    [AvaloniaFact]
    public void The_restart_button_is_hidden_until_a_result_reports_requires_elevation()
    {
        var target = Target();
        using var host = ShowExpanded(out var backend, target);

        var button = ViewHost.Find<Button>(host.Window, b => b.Content as string == "Restart as administrator");
        Assert.False(button.IsVisible);

        backend.NextResult = UnlockerResult.NeedsElevation();
        host.ViewModel.Unlocker.Targets[0].IsSelected = true;
        ViewHost.Find<Button>(host.Window, b => b.Content as string == "Install selected").Command!.Execute(null);
        Assert.True(ViewHost.PumpUntil(() => !host.ViewModel.Unlocker.IsBusy));

        Assert.True(button.IsVisible);
    }

    /// <summary>
    /// The only test that would catch an ItemsControl bound to the wrong collection, or a template
    /// that renders only its first item.
    /// </summary>
    [AvaloniaFact]
    public void Two_targets_render_two_rows()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out _, first, second);

        var boxes = host.Window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.DataContext is UnlockerTargetViewModel).ToList();

        Assert.Equal(2, boxes.Count);
    }

    /// <summary>
    /// The row template's four TextBlocks, read as rendered: name, path, warning and status.
    /// Nothing else in the suite looks at them, so a deleted or re-pointed status TextBlock would
    /// ship in silence, and deleting the warning line leaves every per-target warning invisible
    /// while the view-model tests, which only read the property, stay green.
    /// </summary>
    [AvaloniaFact]
    public void Each_row_renders_its_name_its_path_and_its_status()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out _, first, second);

        foreach (var target in (UnlockerTarget[])[first, second])
        {
            var texts = host.Window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.DataContext is UnlockerTargetViewModel row
                            && row.ClientPath == target.ClientPath)
                .Select(t => t.Text)
                .ToList();

            Assert.Contains(target.DisplayName, texts);
            Assert.Contains(target.ClientPath, texts);
            Assert.Contains("Not installed", texts);
        }

        // Two rows, each with its own warning, both visible at once: warnings are per row because a
        // banner keys by id and a batch would collapse them to whichever target reported last.
        const string ea = "the staged copy failed";
        const string origin = "machine.ini was not found";
        host.ViewModel.Unlocker.Targets[0].Warning = ea;
        host.ViewModel.Unlocker.Targets[1].Warning = origin;
        host.Pump();

        // IsVisible as well as the text: a TextBlock still carries its Text when collapsed, so the
        // text alone would pass with the ObjectConverters.IsNotNull binding deleted or inverted.
        var warnings = ((string[])[ea, origin]).Select(text =>
            ViewHost.Find<TextBlock>(host.Window,
                t => t.DataContext is UnlockerTargetViewModel && t.Text == text)).ToList();
        Assert.All(warnings, w => Assert.True(w.IsVisible));

        // And back: with no warning the line must collapse rather than leave a blank row.
        host.ViewModel.Unlocker.Targets[0].Warning = null;
        host.ViewModel.Unlocker.Targets[1].Warning = null;
        host.Pump();
        Assert.All(warnings, w => Assert.False(w.IsVisible));

        // Rooted at the REGION and matched on the command, not on the window and the caption: the
        // pack list carries its own "Select all" button, so a window-wide search for that caption
        // is satisfied by the wrong control and the unlocker's own button could be deleted in
        // silence. Two different view models own the two commands, which is what makes the
        // identity check decisive.
        var region = UnlockerExpander(host);

        var all = ViewHost.Find<Button>(region, b => b.Content as string == "Select all");
        var none = ViewHost.Find<Button>(region, b => b.Content as string == "Select none");

        Assert.Same(host.ViewModel.Unlocker.SelectAllCommand, all.Command);
        Assert.Same(host.ViewModel.Unlocker.SelectNoneCommand, none.Command);
    }
}
