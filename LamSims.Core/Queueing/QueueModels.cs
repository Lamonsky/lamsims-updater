using System;
using System.Collections.Generic;

namespace LamSims.Core.Queueing;

/// <summary>
/// Where one pack stands. Verifying and Downloading are each skippable, since an archive
/// vouched for by its digest record skips both and an absent archive skips Verifying, so no
/// transition may be treated as mandatory.
/// </summary>
public enum QueueItemState { Queued, Verifying, Downloading, Installing, Completed, Failed, Cancelled, Blocked }

/// <summary>
/// Pausing is the window between Pause() and the active item reaching a point where stopping
/// is honest: a download stops at once, an extract runs to the end of the pack.
/// </summary>
public enum QueueState { Idle, Running, Pausing, Paused }

public sealed record QueueItemSnapshot(
    string Code,
    string Name,
    QueueItemState State,
    long BytesCompleted,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    string? CurrentEntry,
    string? Error,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// The queue is done with this item and will not run it again on its own. True for
    /// Completed, Failed and Cancelled, and for a Blocked item that has already spent its one
    /// retry, which a consumer cannot otherwise tell from a Blocked item still awaiting one.
    /// A re-enqueue is the only thing that revives either.
    /// </summary>
    public bool IsFinal { get; init; }
}

/// <summary>
/// The complete state of the queue. Every update carries all of it, so a consumer replaces its
/// collection wholesale and a dropped update loses nothing.
/// </summary>
public sealed record QueueUpdate(QueueState State, IReadOnlyList<QueueItemSnapshot> Items);

public sealed record QueueOptions
{
    /// <summary>
    /// Minimum gap between progress updates. Zero delivers every one, which is what tests want.
    /// State transitions are never subject to it.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}
