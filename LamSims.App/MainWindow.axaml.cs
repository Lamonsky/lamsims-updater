using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LamSims.App.Services;
using LamSims.App.ViewModels;

namespace LamSims.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private bool _shutdownStarted;
    private bool _shutdownDone;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        OnlyOneSectionOpenAtATime();
    }

    /// <summary>
    /// The two settings sections are mutually exclusive. Not decoration: the region is docked to
    /// the top of a DockPanel, so it takes its full desired height and the pack list and the log
    /// get whatever is left. Measured at the default 640px window, both sections open leaves them
    /// ZERO — arranged past the bottom edge, with no scrollbar anywhere to reach them, because the
    /// scroll deliberately lives inside each growable list rather than around the region as a
    /// whole. One at a time leaves them 71px at worst (fifteen unlocker targets) and 201px at best.
    ///
    /// Here rather than in the view model: nothing outside this window reads which section is open,
    /// and nothing persists it. Wired in the parameterless constructor so it holds for the shipped
    /// window and for a test that builds one directly.
    ///
    /// Closing the other section re-enters this handler with IsExpanded false, which the guard
    /// ignores, so there is no loop to break.
    /// </summary>
    private void OnlyOneSectionOpenAtATime()
    {
        var sections = new[] { this.FindControl<Expander>("AdvancedSection"),
                               this.FindControl<Expander>("UnlockerSection") };

        foreach (var section in sections)
        {
            if (section is null) continue;

            section.PropertyChanged += (sender, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty) return;
                if (e.NewValue is not true) return;

                foreach (var other in sections)
                {
                    if (other is not null && !ReferenceEquals(other, sender)) other.IsExpanded = false;
                }
            };
        }
    }

    public MainWindow(AppServices services) : this()
    {
        // The picker and the clipboard are the two services that need a window handle, which is
        // the whole reason the view models take an interface rather than reaching for either
        // directly.
        _viewModel = new MainViewModel(services with
        {
            Pickers = new StoragePickerService(this),
            Clipboard = new AvaloniaClipboardService(this),
        });

        // The unlocker's elevated relaunch has already shut down by the time it calls this, so both
        // guards are set: OnClosing must not run shutdown a second time, it must just close.
        _viewModel.RequestClose = () =>
        {
            _shutdownStarted = true;
            _shutdownDone = true;
            Close();
        };
        DataContext = _viewModel;

        // Only when the user is already at the bottom: yanking the view down while someone is
        // reading a line further up is worse than not following.
        _viewModel.Log.Lines.CollectionChanged += (_, _) =>
        {
            var scroll = this.FindControl<ScrollViewer>("LogScroll");
            if (scroll is null) return;
            if (scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4)
                scroll.ScrollToEnd();
        };
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_viewModel is not null) await _viewModel.StartAsync(default);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is null || _shutdownDone)
        {
            base.OnClosing(e);
            return;
        }

        // Shutdown waits for the runner, so the close is deferred rather than blocking the UI
        // thread inside the event.
        e.Cancel = true;
        base.OnClosing(e);

        // A second click on the close button re-enters here while the first ShutdownAsync is
        // still waiting for the installer. ShutdownAsync returns immediately on its own
        // IsShuttingDown guard, so without this guard the second entry would set _shutdownDone
        // and Close() mid-extract, exiting the process with the journal marker open and a
        // Partial install on disk, which is the damage this guard exists to prevent. Only the
        // call that started the shutdown may close the window.
        if (_shutdownStarted) return;

        _shutdownStarted = true;

        await _viewModel.ShutdownAsync();
        _shutdownDone = true;
        Close();
    }
}
