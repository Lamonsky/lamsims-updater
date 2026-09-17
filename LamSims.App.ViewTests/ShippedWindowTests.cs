using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LamSims.App;
using LamSims.App.ViewModels;
using LamSims.Core.Logging;
using LamSims.Core.Settings;

namespace LamSims.App.ViewTests;

/// <summary>
/// The one constructor the application actually uses. Every other test in this project builds the
/// window as <c>new MainWindow { DataContext = vm }</c>, which leaves <c>_viewModel</c> null, so
/// <c>OnOpened</c> returns at its null check, <c>OnClosing</c> returns at its own, and the close
/// deferral, its re-entrancy guard and the line that starts the application are all unexecuted.
/// <c>ViewHost.Dispose</c> does call <c>Close()</c>, which is what makes them look covered.
/// </summary>
public class ShippedWindowTests
{
    /// <summary>
    /// The Closed event, not IsVisible: headless leaves IsVisible true after Close(), so a test
    /// watching it cannot tell a deferred close that completed from one that never did.
    /// </summary>
    private static Func<bool> ClosedFlag(MainWindow window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;

        return () => closed;
    }

    private static (MainWindow Window, StubQueue Queue, string Root, AppServices Services) Open()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-shipped").FullName;

        var paths = new AppPaths(Path.Combine(root, "config"));
        paths.EnsureCreated();

        var (services, queue) = ViewHost.Services(paths);

        // No catalog source, no command-line catalog and no cache, so resolution ends Empty and
        // the loader is never asked for a URL. The handler would throw if it were.
        var window = new MainWindow(services);
        window.Show();

        return (window, queue, root, services);
    }

    /// <summary>
    /// Headless does not pump on its own. Needed here (rather than <see cref="ViewHost.Pump"/>,
    /// which is an instance method keyed to a <see cref="ViewHost"/> this class never builds one
    /// of) because the log column's <c>ScrollViewer</c> only reports a real <c>Extent</c>/
    /// <c>Viewport</c> after a layout pass has run.
    /// </summary>
    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void The_shipped_constructor_builds_a_view_model_and_starts_it()
    {
        var (window, queue, root, _) = Open();

        try
        {
            // The pair. A DataContext alone would also hold for the parameterless constructor
            // plus an assignment; "Run" is the half that proves OnOpened's StartAsync was reached.
            Assert.IsType<MainViewModel>(window.DataContext);
            Assert.True(ViewHost.PumpUntil(() => queue.Calls.Contains("Run")),
                "OnOpened never started the queue: " + string.Join(",", queue.Calls));
        }
        finally
        {
            var closed = ClosedFlag(window);
            window.Close();
            ViewHost.PumpUntil(closed);
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Closing_defers_until_shutdown_finishes_and_then_closes_for_real()
    {
        var (window, queue, root, _) = Open();

        try
        {
            Assert.True(ViewHost.PumpUntil(() => queue.Calls.Contains("Run")));

            var closed = ClosedFlag(window);

            // The first Close cancels itself (e.Cancel = true) and awaits ShutdownAsync, because
            // shutdown waits for the runner and must not block the UI thread inside the event.
            // Only the call that started it may close the window, so the window really closing is
            // the observable for the whole deferral, not just for Close having been called.
            window.Close();

            Assert.True(ViewHost.PumpUntil(closed),
                "the deferred close never completed: " + string.Join(",", queue.Calls));

            // The ordering rule, on the shipped path rather than against a recording double:
            // CancelAll then DisposeAsync, and never Complete, which would clear a standing
            // pause and make a blocked pack eligible to start while the process is exiting.
            Assert.Equal(
                ["CancelAll", "Dispose"],
                queue.Calls.Where(c => c is "CancelAll" or "Dispose" or "Complete"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void A_second_close_while_the_first_is_still_shutting_down_does_not_close_the_window()
    {
        var (window, queue, root, _) = Open();

        try
        {
            Assert.True(ViewHost.PumpUntil(() => queue.Calls.Contains("Run")));

            // Hold the shutdown open, or the premise cannot happen: a stubbed ShutdownAsync
            // finishes before a second click can land, the second entry takes the _shutdownDone
            // path instead, and the guard under test is never the thing that decides anything.
            var extracting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.HoldDispose = extracting;

            var closed = ClosedFlag(window);

            window.Close();
            Assert.True(ViewHost.PumpUntil(() => queue.Calls.Contains("Dispose")),
                "shutdown never started: " + string.Join(",", queue.Calls));

            // The second click on the close button, arriving while the first shutdown is still
            // inside DisposeAsync. ShutdownAsync returns at once on its own IsShuttingDown flag,
            // so without the guard this entry sets _shutdownDone and closes the window
            // mid-extract, exiting with the journal marker open and a Partial install on disk.
            window.Close();
            ViewHost.PumpUntil(closed, passes: 2000);

            Assert.False(closed(), "the window closed while its shutdown was still in flight");

            extracting.SetResult();

            Assert.True(ViewHost.PumpUntil(closed), "the close never completed once shutdown ended");
            Assert.Single(queue.Calls, c => c == "CancelAll");
            Assert.Single(queue.Calls, c => c == "Dispose");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The auto-scroll handler MainWindow's real constructor wires up in
    /// <see cref="MainWindow(AppServices)"/>. Only reachable through that constructor — every
    /// other test in this project builds the window as <c>new MainWindow { DataContext = vm }</c>,
    /// which never runs this code at all, per the class doc above.
    /// </summary>
    private static ScrollViewer LogScroll(MainWindow window) =>
        window.FindControl<ScrollViewer>("LogScroll")
        ?? throw new InvalidOperationException("LogScroll not found in the window's visual tree");

    /// <summary>Enough lines, each on its own row, to make the region taller than its viewport —
    /// the premise both scroll tests below need before "already at the bottom" or "scrolled away
    /// from it" means anything.</summary>
    private static void FillPastOneScreen(AppServices services)
    {
        for (var i = 0; i < 200; i++) services.Log.Write(LogLine.Info($"log line {i}"));
    }

    [AvaloniaFact]
    public void A_new_line_keeps_the_log_scrolled_to_the_tail_when_already_at_the_bottom()
    {
        var (window, _, root, services) = Open();

        try
        {
            var scroll = LogScroll(window);

            FillPastOneScreen(services);
            Pump(window);

            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                "the log never became tall enough to scroll, so this test proves nothing");

            // ScrollToEnd() rather than trusting the burst above already left it there: the point
            // under test is what a line written WHILE at the bottom does, not what a burst does.
            scroll.ScrollToEnd();
            Pump(window);

            services.Log.Write(LogLine.Info("the tail line"));
            Pump(window);

            // Recomputed AFTER the write, not before: the new line grows Extent, so the bottom
            // the tail line lives at is further down than where the pre-write bottom was. Checking
            // against the stale, pre-write threshold would pass even if auto-scroll never fired at
            // all, since the untouched offset from before the write already satisfied it.
            var bottomNow = scroll.Extent.Height - scroll.Viewport.Height;

            Assert.True(scroll.Offset.Y >= bottomNow - 4,
                $"a line written at the bottom did not keep the view on the tail: "
                + $"offset={scroll.Offset.Y}, bottom={bottomNow}");
        }
        finally
        {
            var closed = ClosedFlag(window);
            window.Close();
            ViewHost.PumpUntil(closed);
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void A_new_line_does_not_yank_the_log_down_when_scrolled_away_from_the_bottom()
    {
        var (window, _, root, services) = Open();

        try
        {
            var scroll = LogScroll(window);

            FillPastOneScreen(services);
            Pump(window);

            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                "the log never became tall enough to scroll, so this test proves nothing");

            // The user has scrolled up to read an earlier line.
            scroll.ScrollToHome();
            Pump(window);

            var readingOffset = scroll.Offset.Y;
            Assert.True(readingOffset < scroll.Extent.Height - scroll.Viewport.Height - 4,
                "test setup did not actually leave the view scrolled away from the bottom");

            services.Log.Write(LogLine.Info("a line written while the user reads upward"));
            Pump(window);

            Assert.True(Math.Abs(scroll.Offset.Y - readingOffset) < 0.5,
                $"a line written elsewhere yanked the view down: "
                + $"was at {readingOffset}, now at {scroll.Offset.Y}");
        }
        finally
        {
            var closed = ClosedFlag(window);
            window.Close();
            ViewHost.PumpUntil(closed);
            Directory.Delete(root, recursive: true);
        }
    }
}
