using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;

namespace LamSims.App.ViewModels;

/// <summary>
/// The log the window shows. Every member here is touched on the UI thread only: the relay is
/// what crosses threads, and Drain runs inside the dispatcher post it asked for.
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    /// <summary>A bound ring, not upstream's growing string: sixteen connections reporting over a
    /// long session is unbounded otherwise, and the collection is rendered.</summary>
    public const int MaxLines = 2000;

    private readonly LogRelay _relay;
    private readonly IClipboardService _clipboard;

    /// <summary>
    /// Every call to <see cref="Drain"/> increments this once, atomically, before anything else.
    /// That single operation both records "a wake happened" and decides ownership: whoever sees
    /// 0 → 1 owns the loop, and everyone else has already left its mark and can return. The
    /// increment IS the record, which is why there is no second field for a missed wake — a plain
    /// take-and-release flag loses any write that races the loser's return, leaving the relay's
    /// wake spent with nobody scheduled to look again.
    ///
    /// The owner drains, then decrements, and loops while the result is non-zero. Only a decrement
    /// landing on exactly zero means every increment has a matching pass.
    /// </summary>
    private int _pendingWakes;

    public LogViewModel(LogRelay relay, IUiDispatcher dispatcher, IClipboardService clipboard)
    {
        _relay = relay;
        _clipboard = clipboard;
        _relay.OnAvailable = () => dispatcher.Post(Drain);

        // A line written before this constructor ran raised no wake — LogRelay takes the flag only
        // once a handler is attached — so it would sit invisible until some later write surfaced
        // it.
        Drain();
    }

    public ObservableCollection<LogEntry> Lines { get; } = new();

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// The invariant: a wake <see cref="LogRelay"/> has spent is never left with nobody scheduled
    /// to redeem it. See <see cref="_pendingWakes"/> for how that holds under real concurrent
    /// producers, not merely same-stack re-entrancy.
    ///
    /// <see cref="DrainOnce"/> can throw: <see cref="Lines"/> raises CollectionChanged
    /// synchronously into whatever is bound to it. The catch is around the call rather than a
    /// blanket finally, so the decrement below still runs and ownership hands on — a counter stuck
    /// above zero would deafen the view for the rest of the process.
    /// </summary>
    private void Drain()
    {
        if (Interlocked.Increment(ref _pendingWakes) != 1) return;

        do
        {
            try
            {
                DrainOnce();
            }
            catch (Exception ex)
            {
                // Nowhere safer to report it: this loop drains the very log a failure would want
                // to write to, and rethrowing either crashes the process or vanishes into
                // LogRelay.TryWake's catch-all. Trace, not Debug — Debug.WriteLine is
                // [Conditional("DEBUG")] and would leave a Release build silently quiet.
                Trace.WriteLine($"LogViewModel.Drain: DrainOnce threw: {ex}");
            }
        } while (Interlocked.Decrement(ref _pendingWakes) != 0);

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// One pass, trimming as each line lands rather than after: a 100k-line burst must never
    /// balloon <see cref="Lines"/> before the cap applies. Called only by the owner of
    /// <see cref="_pendingWakes"/>'s loop, so this never mutates concurrently.
    ///
    /// <see cref="LogRelay.ReleaseWake"/> is in a finally: a throw partway through would otherwise
    /// leave the relay's wake flag stuck taken — a second, independent way to go permanently deaf
    /// that <see cref="Drain"/>'s own handling does not cover.
    /// </summary>
    private void DrainOnce()
    {
        try
        {
            while (_relay.TryDequeue(out var entry))
            {
                Lines.Add(entry);
                if (Lines.Count > MaxLines) Lines.RemoveAt(0);
            }
        }
        finally
        {
            _relay.ReleaseWake();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>There is no file log, so this is the only way a user gets the log off their
    /// machine to report a bug. Async because Avalonia's clipboard is.</summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = string.Join(Environment.NewLine, Lines.Select(FormatLine));
        await _clipboard.SetTextAsync(text);
    }

    private static string FormatLine(LogEntry entry) =>
        entry.Code is null ? $"{entry.Time}  {entry.Text}" : $"{entry.Time}  {entry.Code}  {entry.Text}";
}
