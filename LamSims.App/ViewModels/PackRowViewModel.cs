using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using LamSims.Core.Scanning;

namespace LamSims.App.ViewModels;

public enum RowMessageKind { None, Warning, Error }

/// <summary>Which colour a row's status text earns. Idle carries none and stays secondary.</summary>
public enum PackStatusKind { Idle, Busy, Ok, Warning, Error }

public sealed partial class PackRowViewModel : ObservableObject
{
    private readonly IQueueController _queue;
    private RelayCommand? _cancelCommand;
    private RelayCommand? _removeCommand;

    public PackRowViewModel(PackEntry entry, IQueueController queue)
    {
        Entry = entry;
        _queue = queue;
    }

    public PackEntry Entry { get; }

    public string Code => Entry.Code;

    public string Name => Entry.Name;

    public string SizeText => FormatBytes(Entry.Size);

    public PackInstallState InstallState { get; private set; } = PackInstallState.NotInstalled;

    public IReadOnlyList<string> MissingDirs { get; private set; } = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckable))]
    private bool _forceCheckable;

    [ObservableProperty]
    private bool _isChecked;

    private long _bytesCompleted;
    private long _totalBytes;
    private double _bytesPerSecond;
    private TimeSpan? _eta;
    private string? _entry;
    private string? _error;
    private IReadOnlyList<string> _warnings = Array.Empty<string>();
    private string? _keptMessage;
    private RowMessageKind _keptKind = RowMessageKind.None;

    /// <summary>The terminal state a scan most recently retired. Latches: every snapshot carrying
    /// it is ignored until a different state arrives, which a real re-enqueue always produces.</summary>
    private QueueItemState? _retiredState;

    /// <summary>The queue's own verdict that it will not run this item again by itself.</summary>
    private bool _isFinal;

    /// <summary>Null when the pack is not in the queue, or when a scan cleared a terminal overlay.</summary>
    public QueueItemState? QueueState { get; private set; }

    /// <summary>Blocked as well as absent: Enqueue accepts a re-enqueue of a blocked item, which
    /// is what MarkBlocked's text tells the user to do, and the checkbox is the only way.</summary>
    public bool IsCheckable =>
        (QueueState is null || QueueState is QueueItemState.Blocked)
        && (ForceCheckable || InstallState != PackInstallState.Installed);

    public bool IsBusy => QueueState
        is QueueItemState.Verifying or QueueItemState.Downloading or QueueItemState.Installing;

    public bool IsProgressVisible => IsBusy && _totalBytes > 0;

    public double ProgressPercent => _totalBytes > 0 ? _bytesCompleted * 100d / _totalBytes : 0;

    /// <summary>Blanked on every terminal state: the snapshot's field is sticky.</summary>
    public string? CurrentEntry => IsTerminal(QueueState) ? null : _entry;

    public bool CanCancel => QueueState
        is QueueItemState.Queued or QueueItemState.Verifying
        or QueueItemState.Downloading or QueueItemState.Installing;

    // Blocked offers neither, and IsFinal does not change that: Cancel and Remove both refuse a
    // pack the queue is not running, so either control would silently no-op. Re-enqueueing is the
    // escape core honours, and IsCheckable is where it is offered.
    public bool CanRemove => QueueState is QueueItemState.Queued;

    // Failed before the warnings arm: a failed download carries the quarantine warning alongside
    // it, and reversed, a quarantined archive whose re-download 404s would read as a warning and
    // never show why it failed.
    public string? Message => QueueState switch
    {
        QueueItemState.Blocked => null,
        null => _keptMessage,
        QueueItemState.Failed => FailedMessage,
        _ when _warnings.Count > 0 => string.Join(" ", _warnings),
        _ => null,
    };

    private string? FailedMessage =>
        _error is null
            ? (_warnings.Count > 0 ? string.Join(" ", _warnings) : null)
            : string.Join(" ", _warnings.Prepend(_error));

    public RowMessageKind MessageKind => QueueState switch
    {
        QueueItemState.Blocked => RowMessageKind.None,
        null => _keptKind,
        QueueItemState.Failed => RowMessageKind.Error,
        _ when _warnings.Count > 0 => RowMessageKind.Warning,
        _ => RowMessageKind.None,
    };

    public bool IsWarning => MessageKind is RowMessageKind.Warning;

    public bool IsError => MessageKind is RowMessageKind.Error;

    public string StatusText => QueueState switch
    {
        QueueItemState.Queued => "Queued",
        QueueItemState.Verifying => "Verifying…",
        QueueItemState.Downloading => $"Downloading  {ProgressPercent:0}%{RateText}{EtaText}",
        QueueItemState.Installing => $"Installing  {ProgressPercent:0}%",
        // Core's text, verbatim, with nothing invented behind it. MarkBlocked sets
        // Error on every path that produces Blocked, so the null case is unreachable; where it
        // is not, an empty row is honest and a sentence this application made up is not.
        QueueItemState.Blocked => _error ?? string.Empty,
        QueueItemState.Failed => "Failed",
        QueueItemState.Cancelled => "Cancelled",
        QueueItemState.Completed => "Installed",
        _ => ScanText,
    };

    /// <summary>The colour behind <see cref="StatusText"/>, keyed off the same states in the same
    /// order so the two cannot disagree. The <c>_</c> arm is a scanned-but-not-queued row.</summary>
    public PackStatusKind StatusKind => QueueState switch
    {
        QueueItemState.Verifying or QueueItemState.Downloading or QueueItemState.Installing
            => PackStatusKind.Busy,
        QueueItemState.Completed => PackStatusKind.Ok,
        QueueItemState.Failed or QueueItemState.Blocked => PackStatusKind.Error,
        QueueItemState.Queued or QueueItemState.Cancelled => PackStatusKind.Idle,
        _ => ScanKind,
    };

    private PackStatusKind ScanKind => InstallState switch
    {
        PackInstallState.Installed => PackStatusKind.Ok,
        PackInstallState.InstalledUnverified or PackInstallState.Partial => PackStatusKind.Warning,
        _ => PackStatusKind.Idle,
    };

    // Four bools rather than the enum: MainWindowBindingTests resolves every binding path as a
    // single member of its DataContext, and a Classes.x binding has nowhere to put a converter.
    public bool IsStatusOk => StatusKind is PackStatusKind.Ok;

    public bool IsStatusBusy => StatusKind is PackStatusKind.Busy;

    public bool IsStatusWarning => StatusKind is PackStatusKind.Warning;

    public bool IsStatusError => StatusKind is PackStatusKind.Error;

    private string RateText => _bytesPerSecond > 0 ? $"  {FormatBytes((long)_bytesPerSecond)}/s" : "";

    private string EtaText => _eta is { } eta ? $"  {FormatEta(eta)}" : "";

    public void ApplyQueue(QueueItemSnapshot snapshot)
    {
        // The queue republishes every item whenever its OWN state moves, so a terminal item echoes
        // after the publish that carried its ending — and without this the echo reapplies what the
        // scan just retired. Latched, because the republishing continues all session; released on
        // a different state, so a real second run is never suppressed.
        if (_retiredState == snapshot.State) return;
        _retiredState = null;

        QueueState = snapshot.State;
        _bytesCompleted = snapshot.BytesCompleted;
        _totalBytes = snapshot.TotalBytes;
        _bytesPerSecond = snapshot.BytesPerSecond;
        _eta = snapshot.Eta;
        _entry = snapshot.CurrentEntry;
        _error = snapshot.Error;
        _warnings = snapshot.Warnings;
        _isFinal = snapshot.IsFinal;
        _keptMessage = null;
        _keptKind = RowMessageKind.None;

        if (!IsCheckable) IsChecked = false;

        RaiseAll();
    }

    /// <summary>The pack is absent from the update. Unlike the scan's clear this drops the message
    /// too: an absent item has no history to explain.</summary>
    public void ClearQueueOverlay()
    {
        // Called on every absent row of every update: with a 30-pack catalog and one download
        // that is thousands of no-op RaiseAll()s a second on the UI thread. These four are the
        // whole of what this method clears.
        if (QueueState is null && _retiredState is null
            && _keptMessage is null && _keptKind == RowMessageKind.None)
        {
            return;
        }

        DropOverlay();
        _keptMessage = null;
        _keptKind = RowMessageKind.None;
        _retiredState = null;
        RaiseAll();
    }

    private void DropOverlay()
    {
        QueueState = null;
        _bytesCompleted = 0;
        _totalBytes = 0;
        _bytesPerSecond = 0;
        _eta = null;
        _entry = null;
        _error = null;
        _warnings = Array.Empty<string>();
        _isFinal = false;
    }

    private static bool IsTerminal(QueueItemState? state) =>
        state is QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled;

    private bool IsFinished => IsTerminal(QueueState) || _isFinal;

    private static string FormatEta(TimeSpan eta) =>
        eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
        : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m"
        : $"{eta.Seconds}s";

    public IRelayCommand CancelCommand => _cancelCommand ??=
        new RelayCommand(() => _queue.Cancel(Code), () => CanCancel);

    // The return value is deliberately dropped: Remove refuses the active item, the UI cannot
    // tell which item that is, and the next update says what actually happened.
    public IRelayCommand RemoveCommand => _removeCommand ??=
        new RelayCommand(() => _queue.Remove(Code), () => CanRemove);

    /// <summary>An Installed row's checkbox is cleared and disabled; this is the only way past
    /// that, and it does not check the box. A command rather than a settable property, because a
    /// MenuItem cannot write one.</summary>
    [RelayCommand]
    private void Reinstall() => ForceCheckable = true;

    // private, not protected: the class is sealed, and a new protected member in a sealed type
    // is CS0628, which -warnaserror turns into a build failure.
    private string ScanText => InstallState switch
    {
        PackInstallState.Installed => "Installed",
        PackInstallState.InstalledUnverified => "Installed (not verified by this tool)",
        PackInstallState.Partial when MissingDirs.Count == 0 =>
            "Partial — an install started and did not finish",
        PackInstallState.Partial =>
            $"Partial — {MissingDirs.Count} folder{(MissingDirs.Count == 1 ? "" : "s")} missing",
        _ => "Not installed",
    };

    public void ApplyScan(PackScanResult result)
    {
        InstallState = result.State;
        MissingDirs = result.MissingDirs;

        // A terminal row lets go of the queue's word for it at the next scan, so a finished,
        // failed or cancelled pack reads disk state rather than pinning the queue's verdict for
        // the rest of the session. Its message is kept, since the error is what explains the row.
        if (IsFinished)
        {
            _keptMessage = Message;
            _keptKind = MessageKind;
            _retiredState = QueueState;
            DropOverlay();
        }

        if (!IsCheckable) IsChecked = false;

        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in (string[])
        [
            nameof(InstallState), nameof(MissingDirs), nameof(QueueState), nameof(IsCheckable),
            nameof(StatusText), nameof(IsBusy), nameof(ProgressPercent), nameof(IsProgressVisible),
            nameof(Message), nameof(MessageKind), nameof(IsWarning), nameof(IsError),
            nameof(StatusKind), nameof(IsStatusOk), nameof(IsStatusBusy),
            nameof(IsStatusWarning), nameof(IsStatusError),
            nameof(CurrentEntry), nameof(CanCancel), nameof(CanRemove),
        ])
        {
            OnPropertyChanged(name);
        }

        _cancelCommand?.NotifyCanExecuteChanged();
        _removeCommand?.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        // InvariantCulture: the tests assert "12.4 GB" and "8.1 MB/s" literally, and on a machine
        // whose culture uses a comma decimal separator the ambient culture would fail them.
        return unit == 0
            ? $"{bytes} B"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", value, units[unit]);
    }
}
