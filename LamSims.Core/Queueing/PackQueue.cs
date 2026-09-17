using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;

namespace LamSims.Core.Queueing;

/// <summary>
/// Runs packs one at a time, publishing the queue's state after every change. Sequencing is
/// correctness, not policy: two runs of one pack fight over its chunk sidecar and archive. A
/// second process is held off by <see cref="PackLock"/> instead.
///
/// Controls are callable from any thread. A queue that has been run must be disposed:
/// <see cref="DisposeAsync"/> is the only thing that releases the linked source.
/// </summary>
public sealed class PackQueue : IAsyncDisposable
{
    private sealed class Item
    {
        /// <summary>Settable so a retry runs against the refreshed catalog entry, not the dead
        /// mirrors it just failed against.</summary>
        public required PackEntry Pack { get; set; }
        public required string GameDirectory { get; set; }
        public QueueItemState State { get; set; } = QueueItemState.Queued;
        public long BytesCompleted { get; set; }
        public long TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan? Eta { get; set; }
        public string? CurrentEntry { get; set; }
        public string? Error { get; set; }
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
        public int BlockedAttempts { get; set; }

        /// <summary>What <see cref="_completedCount"/> read when this item was last refused its
        /// lock, so its one free retry is spent only after another pack has actually run.</summary>
        public long BlockedAtCount { get; set; }

        /// <summary>A pause, not the user, cancelled this item: Settle requeues it.</summary>
        public bool PausedOut { get; set; }

        /// <summary>Zero, not long.MinValue: `now - MinValue` overflows (no
        /// CheckForOverflowUnderflow here) and the sentinel would never clear.</summary>
        public long LastProgressTicks { get; set; }

        public QueueItemSnapshot ToSnapshot() => new(
            Pack.Code, Pack.Name, State, BytesCompleted, TotalBytes,
            BytesPerSecond, Eta, CurrentEntry, Error, Warnings)
        {
            IsFinal = IsTerminal(this),
        };
    }

    /// <summary>
    /// One iteration's cancellation source, wrapped so tripping and disposing are serialised.
    /// Cancel must never run under <see cref="_gate"/>: it invokes every registration
    /// synchronously — HTTP deadlines, connection teardown, timers — blocking every control for
    /// as long as that takes. Cancelling outside the lock instead would race the loop's Dispose,
    /// which is undefined behaviour rather than an exception.
    ///
    /// <see cref="_g"/> is a leaf lock, never taken under <see cref="_gate"/> and never taking
    /// it. Hence <see cref="IsCancellationRequested"/> is a bare field read: taking _g under
    /// _gate would invert the order against a cancellation callback re-entering the queue. Token
    /// is unsafe after Dispose and is read only by the loop that owns the run.
    /// </summary>
    private sealed class ActiveRun(CancellationTokenSource cts)
    {
        private readonly Lock _g = new();
        private bool _retired;

        public CancellationToken Token => cts.Token;

        public bool IsCancellationRequested => cts.IsCancellationRequested;

        /// <summary>A no-op once the run has been retired, rather than undefined behaviour.</summary>
        public void Cancel()
        {
            lock (_g)
                if (!_retired)
                    cts.Cancel();
        }

        public void Retire()
        {
            lock (_g)
            {
                _retired = true;
                cts.Dispose();
            }
        }
    }

    private readonly IPackRunner _runner;
    private readonly DownloadPaths _paths;
    private readonly QueueOptions _options;

    private readonly Channel<QueueUpdate> _updates =
        Channel.CreateUnbounded<QueueUpdate>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _gate = new();
    private readonly List<Item> _items = new();
    private readonly SemaphoreSlim _signal = new(0);

    private QueueState _state = QueueState.Idle;
    private bool _completed;
    private int _started;
    private int _disposed;

    /// <summary>The run's source, linked to <see cref="RunAsync"/>'s token. Volatile on both
    /// sides because <see cref="DisposeAsync"/> reads it from another thread, and null until the
    /// run reaches the assignment.</summary>
    private CancellationTokenSource? _loop;

    /// <summary>Completed as <see cref="RunAsync"/>'s finally's last act, so awaiting it means
    /// the loop stopped, the pack lock is released and the channel is closed.</summary>
    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The item in flight and the run that stops it, or null between items. Written and
    /// cleared together under <see cref="_gate"/>, so a control never sees one without the other.
    /// The loop may already have retired the run by the time a control cancels; the run's leaf
    /// lock serialises the two, so either order is safe.</summary>
    private ActiveRun? _active;
    private Item? _activeItem;

    /// <summary>A pause the loop has not acted on. Distinct from <c>Pausing</c> because Resume
    /// clears it: an install finishing after the user changed their mind must not park the queue.</summary>
    private bool _pauseRequested;

    /// <summary>Run attempts ended, counted on every exit from RunItemAsync including faults, so
    /// RequeueBlocked can tell "work happened" from "nothing happened at all".</summary>
    private long _completedCount;

    public PackQueue(IPackRunner runner, DownloadPaths paths, QueueOptions? options = null)
    {
        _runner = runner;
        _paths = paths;
        _options = options ?? new QueueOptions();
    }

    public IAsyncEnumerable<QueueUpdate> Updates => _updates.Reader.ReadAllAsync();

    public void Enqueue(PackEntry pack, string gameDirectory)
    {
        lock (_gate)
        {
            if (_completed) return;

            var existing = _items.FirstOrDefault(i => i.Pack.Code == pack.Code);

            if (existing is null)
            {
                _items.Add(new Item { Pack = pack, GameDirectory = gameDirectory });
            }
            else if (IsTerminal(existing) || existing.State == QueueItemState.Blocked)
            {
                // Re-enqueueing a finished item is the retry: it goes to the back, history cleared.
                // A blocked item is pending, not terminal, but counts as an explicit "retry now" —
                // in a long-lived shell RequeueBlocked may never fire, so ignoring it would make
                // the user's only action a silent no-op.
                Reset(existing, pack, gameDirectory);
                _items.Remove(existing);
                _items.Add(existing);
            }
            else
            {
                return;   // pending or active: already going to run
            }
        }

        Publish();
        _signal.Release();
    }

    /// <summary>
    /// No more work is coming: finish what is queued and stop. Clears the pause rather than
    /// respecting it — a standing pause with a Queued item parks the loop on its signal forever,
    /// and every consumer then waits on a channel nobody will close.
    /// </summary>
    public void Complete()
    {
        // Only on the transition; TakeNext publishes the Idle that follows.
        if (StopAcceptingWork()) Publish();

        _signal.Release();
    }

    /// <summary>
    /// Shared by <see cref="Complete"/> and <see cref="DisposeAsync"/>. Returns whether the queue
    /// came out of a pause — the only case worth publishing. Disposal ignores it, because the last
    /// update a consumer should see is <see cref="RunAsync"/>'s finally's Idle.
    /// </summary>
    private bool StopAcceptingWork()
    {
        lock (_gate)
        {
            _completed = true;
            _pauseRequested = false;

            var resumed = _state is QueueState.Paused or QueueState.Pausing;
            if (resumed) _state = QueueState.Running;
            return resumed;
        }
    }

    /// <summary>
    /// Stops the queue. A download stops now and returns to Queued — cancellation plus the .part
    /// file is the only pause the engine has. An extract finishes, since it is not resumable, so
    /// the pause lands at the item boundary.
    ///
    /// NEVER cache a ProgressTracker across a pause: cancellation matches no catch filter in
    /// ChunkFetcher, so its provisional bytes are never abandoned and progress inflates
    /// permanently. A fresh tracker per DownloadAsync call is what discards them.
    ///
    /// Refused after Complete(): a pause with queued work parks the loop, and no Enqueue is left
    /// to release it.
    /// </summary>
    public void Pause()
    {
        ActiveRun? run = null;

        lock (_gate)
        {
            if (_completed) return;
            if (_state is QueueState.Pausing or QueueState.Paused) return;

            _pauseRequested = true;

            if (_activeItem is null)
            {
                // Nothing in flight, so there is no boundary to wait for.
                _state = QueueState.Paused;
            }
            else if (_activeItem.State == QueueItemState.Installing || IsTerminal(_activeItem))
            {
                // Both land at the item boundary. Stamping PausedOut on a terminal item would
                // requeue a pack that finished on its own.
                _state = QueueState.Pausing;
            }
            else
            {
                _state = QueueState.Pausing;

                // An already-tripped token belongs to someone whose intent outranks a pause: a
                // user Cancel, or shutdown. Re-stamping PausedOut would requeue a pack the user
                // cancelled. The window is the whole runner unwind, not an instant.
                if (!_active!.IsCancellationRequested)
                {
                    _activeItem.PausedOut = true;
                    run = _active;
                }
            }
        }

        Publish();

        // Not atomic with the PausedOut write, and must stay that way: see ActiveRun.
        run?.Cancel();
    }

    /// <summary>
    /// Also cancels a pause that has not landed yet, so a user who changes their mind during an
    /// install does not have to press this twice.
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_state is not (QueueState.Paused or QueueState.Pausing)) return;

            _pauseRequested = false;
            _state = QueueState.Running;
        }

        Publish();
        _signal.Release();
    }

    /// <summary>Ends one pack. The active one stops through its token so Settle records the
    /// ending; a pending one is marked here. A terminal one is ignored — rewriting a Completed
    /// pack as Cancelled would misreport work that happened.</summary>
    public void Cancel(string code)
    {
        ActiveRun? run = null;

        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Pack.Code == code);
            if (item is null || IsTerminal(item)) return;

            if (ReferenceEquals(item, _activeItem))
            {
                // Cleared, not left alone: a pause that just set it would turn this into a requeue.
                item.PausedOut = false;
                run = _active;
            }
            else
            {
                item.State = QueueItemState.Cancelled;
            }
        }

        Publish();

        // Cancelling the last Queued item can flip Finished(), and a parked loop has nothing else
        // to wake it. A spurious permit costs nothing: TakeNext absorbs it.
        _signal.Release();
        run?.Cancel();
    }

    public void CancelAll()
    {
        ActiveRun? run = null;
        var changed = false;

        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (IsTerminal(item)) continue;

                changed = true;

                if (ReferenceEquals(item, _activeItem))
                {
                    item.PausedOut = false;
                    run = _active;
                }
                else
                {
                    // Includes a blocked item awaiting retry, which RequeueBlocked would resurrect.
                    item.State = QueueItemState.Cancelled;
                }
            }
        }

        if (!changed) return;

        Publish();
        _signal.Release();
        run?.Cancel();
    }

    /// <summary>Takes a pack out of the list. Refuses the active one, which would run on as a
    /// ghost holding its lock, and a terminal one, which the user keeps in view.</summary>
    public bool Remove(string code)
    {
        bool removed;

        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Pack.Code == code);
            removed = item is not null && !IsTerminal(item) && !ReferenceEquals(item, _activeItem);
            if (removed) _items.Remove(item!);
        }

        if (removed)
        {
            Publish();

            _signal.Release();
        }

        return removed;
    }

    /// <summary>
    /// Drives the queue until it drains after <see cref="Complete"/>, until <paramref name="ct"/>
    /// trips, or until <see cref="DisposeAsync"/> stops it. Returns normally in all three — a
    /// queue told to stop has not failed. Callable once, and refused after disposal.
    ///
    /// The linked source is deliberately NOT disposed here: <see cref="DisposeAsync"/> cancels
    /// through it, and Cancel on a disposed source throws. A caller that never disposes leaks one
    /// source plus its registration on <paramref name="ct"/> per run — small, but unbounded
    /// against a long-lived token.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // Outside the try, and the only thing that is: a second caller must not run the finally
        // and close a channel the first owns.
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("This queue has already been run.");

        // Assigned inside the try, not before it: _started is already set, so any throw before the
        // try would hang every later disposal on a _finished nobody completes.
        CancellationTokenSource? loop = null;

        try
        {
            // Refused, not quietly started: the loop would take pack locks and install packs for a
            // queue the shell has let go of. Inside the try because this caller owns the finally.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            // Volatile because a concurrent DisposeAsync reads it; one landing in the gap finds null
            // and degrades to "drain what is queued", which still terminates.
            loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _loop, loop);

            // Inside the try because this throws on a read-only root or a denied permission, and
            // that fault would otherwise skip the finally and strand every consumer. First,
            // because PackLock needs the root before SegmentedDownloader would create it.
            _paths.EnsureCreated();

            await LoopAsync(loop.Token);
        }
        catch (OperationCanceledException) when (loop?.IsCancellationRequested == true)
        {
            // Filtered on the loop source, not ct: a queue stopped by DisposeAsync has not failed
            // either, and ct alone would let that escape as a fault out of `await run`.
        }
        finally
        {
            // RunAsync owns completion on every exit path; two closers would race.
            lock (_gate) _state = QueueState.Idle;
            Publish();
            _updates.Writer.TryComplete();

            // Last, so a DisposeAsync released by this has nothing left to wait for.
            _finished.TrySetResult();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Created before selection so TakeNext can register it under the same lock that picks
            // the item. One per iteration, never reused: a source a pause already cancelled would
            // stop the resumed run before it began. The finally makes Retire unmissable, and
            // Retire is what turns a late Cancel into a no-op rather than a race with Dispose.
            var run = new ActiveRun(CancellationTokenSource.CreateLinkedTokenSource(ct));

            try
            {
                var next = TakeNext(run, out var stateChanged);

                // In a long-lived shell RunAsync's finally never arrives, so without this the last
                // update a consumer sees says Running with every item terminal, indefinitely.
                if (stateChanged) Publish();

                if (next is null)
                {
                    // Before Finished and before the wait: the queue going quiet is exactly when a
                    // pack refused its lock is owed its retry.
                    if (RequeueBlocked()) continue;
                    if (Finished()) return;

                    await _signal.WaitAsync(ct);
                    continue;
                }

                await RunItemAsync(next, run);
            }
            finally
            {
                run.Retire();
            }
        }
    }

    /// <summary>
    /// The next item to run, or null, and where the run is registered so controls can reach it.
    /// Registration must stay inside the lock that selects the item: any gap lets Pause jump to
    /// Paused while the item runs on, Cancel be overwritten by Settle, and Remove leave a ghost.
    ///
    /// The pause check leads, and is what keeps the loop from spinning: a pause is the one thing
    /// that returns an item to Queued, so without it the next pass finds the same item and floods
    /// an unbounded channel.
    ///
    /// <paramref name="stateChanged"/> reports whether the queue's state moved, so the caller
    /// publishes the transition and nothing else. Assigned in a finally because of three exits.
    /// </summary>
    private Item? TakeNext(ActiveRun run, out bool stateChanged)
    {
        lock (_gate)
        {
            var previous = _state;

            try
            {
                if (_pauseRequested || _state is QueueState.Paused or QueueState.Pausing) return null;

                var next = _items.FirstOrDefault(i => i.State == QueueItemState.Queued);
                if (next is null)
                {
                    _state = QueueState.Idle;
                    return null;
                }

                _state = QueueState.Running;
                _active = run;
                _activeItem = next;

                // A stale flag would make Settle read this run's ending as a requeue.
                next.PausedOut = false;
                return next;
            }
            finally
            {
                stateChanged = _state != previous;
            }
        }
    }

    /// <summary>
    /// Nothing left to do and no more coming. The blocked clause covers a real few-instruction
    /// window: RequeueBlocked skips a once-blocked item with <see cref="_completed"/> still false,
    /// releases the gate, Complete() flips it, and without the clause the channel would close on a
    /// pack promising a retry it never got. It cannot park the loop, because reaching here that way
    /// means Complete()'s permit is already pending.
    /// </summary>
    private bool Finished()
    {
        lock (_gate)
            return _completed
                && !_items.Any(i => i.State == QueueItemState.Queued)
                && !_items.Any(i => i.State == QueueItemState.Blocked && i.BlockedAttempts == 1);
    }

    /// <summary>
    /// Runs one item under its pack lock, or records that another copy of the application holds it.
    /// The lock spans verify, download and install together, since a second instance taking the
    /// pack between phases would share its chunk sidecar and delete an archive still extracting.
    ///
    /// The acquire is inside the try and both outcomes branch inside it, because deregistration,
    /// the deferred pause and the publish all live in the finally and must have one home. A
    /// throwing acquire becomes a Failed item; above the try it would skip the finally, leaving
    /// _activeItem naming a retired run forever — Remove refuses the pack, Cancel no-ops, and the
    /// last snapshot says Queued with no error at all.
    /// </summary>
    private async Task RunItemAsync(Item item, ActiveRun run)
    {
        PackLock? held = null;
        var blocked = false;

        try
        {
            // The only signal of contention there is. PackLock leaves its file on disk when
            // released, so nothing here may infer state from that file or delete one.
            held = PackLock.TryAcquire(_paths, item.Pack.Code);
            blocked = held is null;

            if (blocked) MarkBlocked(item);
            else Settle(item, await InvokeRunnerAsync(item, run.Token));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Cancellation is already handled: InvokeRunnerAsync turns this run's token into a
            // Cancelled result and any other OperationCanceledException into a Failed. Anything
            // reaching here belongs to a shutdown and must not land "The operation was canceled."
            // in the user's error column.
            lock (_gate)
            {
                item.State = QueueItemState.Failed;
                item.Error = e.Message;
            }
        }
        finally
        {
            // Before the publish, so no snapshot claims the pack is done while its lock is held.
            held?.Dispose();

            lock (_gate)
            {
                _active = null;
                _activeItem = null;

                // Not raised on the blocked path; see MarkBlocked. A throwing acquire does count,
                // since blocked stays false there.
                if (!blocked)
                {
                    _completedCount++;

                    // The free retry is per contiguous run of refusals, not per item lifetime: a
                    // pack requeued by a pause must not carry a spent attempt into a later block.
                    item.BlockedAttempts = 0;
                    item.BlockedAtCount = 0;
                }

                // Where a deferred pause lands. Reading _pauseRequested rather than _state is what
                // lets a Resume during Pausing undo it without reaching into the running item.
                if (_pauseRequested) _state = QueueState.Paused;
            }

            Publish();
        }
    }

    private void MarkBlocked(Item item)
    {
        lock (_gate)
        {
            item.BlockedAttempts++;
            item.State = QueueItemState.Blocked;

            // Stamped, not incremented: raising _completedCount here would burn the free retry on
            // the next pass, against the same still-held lock.
            item.BlockedAtCount = _completedCount;

            // Neither half may promise a retry that is not scheduled: in a long-lived shell the
            // rest of the queue may never produce anything to wait for.
            item.Error =
                $"Another copy of this application is working on '{item.Pack.Code}'. "
                + (item.BlockedAttempts >= 2
                    ? "It was still busy when this pack was retried, so the pack was left alone; queue it again once that copy has finished."
                    : "It will be retried once the rest of the queue has run — or queue it again to retry it right away.");
        }
    }

    /// <summary>
    /// Gives a once-blocked item one more go, but only when the wait could have helped: another
    /// item finished, or no more work is coming. Retrying immediately would burn the free attempt
    /// against the same still-held lock. A lone blocked pack in an open queue therefore waits —
    /// which is why Enqueue treats a re-enqueue as an explicit "retry now". Terminates because the
    /// attempt count only rises.
    /// </summary>
    private bool RequeueBlocked()
    {
        var requeued = false;

        lock (_gate)
        {
            if (_items.Any(i => i.State == QueueItemState.Queued)) return false;

            foreach (var item in _items)
            {
                if (item.State != QueueItemState.Blocked || item.BlockedAttempts != 1) continue;
                if (!_completed && _completedCount == item.BlockedAtCount) continue;

                item.State = QueueItemState.Queued;
                item.Error = null;
                requeued = true;
            }
        }

        if (requeued) Publish();
        return requeued;
    }

    private async Task<PackWorkflowResult?> InvokeRunnerAsync(Item item, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(
                item.Pack,
                item.GameDirectory,
                new PhaseSink(this, item),
                new DownloadSink(this, item),
                new InstallSink(this, item),
                ct);

            // A null result is not "already handled": left Queued, TakeNext picks it straight back
            // up and floods an unbounded channel. Fail it so null has one meaning for Settle.
            if (result is null)
            {
                lock (_gate)
                {
                    item.State = QueueItemState.Failed;
                    item.Error = "The runner returned no result.";
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Result-shaped, so Settle stays the one place that decides what a cancellation means.
            // The filter discriminates on WHICH token tripped: an HTTP read timeout raises the
            // same exception type and must fall through to the catch below as a genuine failure.
            return new PackWorkflowResult(
                PackStage.Downloading, DownloadResult.Cancelled(), null, Array.Empty<string>());
        }
        catch (Exception e)
        {
            // A runner exception never kills the queue: no runner is provably result-shaped.
            lock (_gate)
            {
                item.State = QueueItemState.Failed;
                item.Error = e.Message;
            }

            return null;
        }
    }

    private void Settle(Item item, PackWorkflowResult? result)
    {
        // Null has one meaning: InvokeRunnerAsync already marked the item Failed.
        if (result is null) return;

        lock (_gate)
        {
            item.Warnings = result.Warnings;

            item.State = result switch
            {
                { Install.Outcome: InstallOutcome.Installed } => QueueItemState.Completed,
                { Install.Outcome: InstallOutcome.Cancelled } => QueueItemState.Cancelled,
                { Install: not null } => QueueItemState.Failed,
                { Download.Outcome: DownloadOutcome.Cancelled } => QueueItemState.Cancelled,
                { Download: not null } => QueueItemState.Failed,
                // Unreachable backstop. Failed rather than a throw, so a future stage that forgets
                // one degrades into a visible error instead of killing the loop.
                _ => QueueItemState.Failed,
            };

            item.Error ??= result.Install?.Error ?? result.Download?.Error;

            // A pause is not an ending, and this is the only place the conversion happens — under
            // the same lock that set Cancelled, so no consumer sees the paused pack as cancelled.
            if (item.State == QueueItemState.Cancelled && item.PausedOut)
            {
                item.State = QueueItemState.Queued;
                item.PausedOut = false;

                // A resume rebuilds its baseline from committed chunks alone, so every byte in an
                // uncommitted chunk is refetched; keeping the figure would make BytesCompleted
                // fall on resume.
                item.BytesCompleted = 0;
                item.TotalBytes = 0;
                item.BytesPerSecond = 0;
                item.Eta = null;
                item.CurrentEntry = null;
            }
        }
    }

    private static bool IsTerminal(Item item) =>
        item.State is QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled
        || (item.State == QueueItemState.Blocked && item.BlockedAttempts >= 2);

    private static void Reset(Item item, PackEntry pack, string gameDirectory)
    {
        // Against the entry the caller just handed us: by retry time the shell may have refreshed
        // the catalog with live mirrors and a corrected digest.
        item.Pack = pack;
        item.GameDirectory = gameDirectory;
        item.State = QueueItemState.Queued;
        item.BytesCompleted = 0;
        item.TotalBytes = 0;
        item.BytesPerSecond = 0;
        item.Eta = null;
        item.CurrentEntry = null;
        item.Error = null;
        item.Warnings = Array.Empty<string>();
        item.BlockedAttempts = 0;
        item.BlockedAtCount = 0;
        item.PausedOut = false;
        item.LastProgressTicks = 0;
    }

    /// <summary>
    /// Builds and writes under ONE lock acquisition. Split, two threads can each build a
    /// consistent snapshot and then write them transposed, so a consumer sees a newer state
    /// followed by an older one. TryWrite never blocks and never throws on a closed channel.
    /// </summary>
    private void Publish()
    {
        lock (_gate)
            _updates.Writer.TryWrite(
                new QueueUpdate(_state, _items.Select(i => i.ToSnapshot()).ToArray()));
    }

    /// <summary>The interval check must come FIRST and short-circuit before any snapshot is
    /// built: the reporting worker can be pinned in ProgressTracker's drain loop for an unbounded
    /// number of sibling deposits, and only the cheap comparison mitigates that.</summary>
    private void PublishProgress(Item item)
    {
        if (_options.ProgressInterval > TimeSpan.Zero)
        {
            var now = Environment.TickCount64;
            lock (_gate)
            {
                if (now - item.LastProgressTicks < (long)_options.ProgressInterval.TotalMilliseconds) return;
                item.LastProgressTicks = now;
            }
        }

        Publish();
    }

    private sealed class PhaseSink(PackQueue queue, Item item) : IProgress<PackPhase>
    {
        public void Report(PackPhase value)
        {
            lock (queue._gate)
                item.State = value switch
                {
                    PackPhase.Verifying => QueueItemState.Verifying,
                    PackPhase.Downloading => QueueItemState.Downloading,
                    _ => QueueItemState.Installing,
                };

            queue.Publish();   // a transition is never rate-limited
        }
    }

    private sealed class DownloadSink(PackQueue queue, Item item) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            lock (queue._gate)
            {
                item.BytesCompleted = value.BytesCompleted;
                item.TotalBytes = value.TotalBytes;
                item.BytesPerSecond = value.BytesPerSecond;
                item.Eta = value.Eta;
            }

            queue.PublishProgress(item);
        }
    }

    private sealed class InstallSink(PackQueue queue, Item item) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value)
        {
            lock (queue._gate)
            {
                item.BytesCompleted = value.BytesWritten;
                item.TotalBytes = value.TotalBytes;
                item.CurrentEntry = value.CurrentEntry;
            }

            queue.PublishProgress(item);
        }
    }

    /// <summary>
    /// Stops the queue and waits for the item in flight to actually stop before its pack lock is
    /// released. Cancel alone is not enough: ZipInstaller checks its token once per entry, so a
    /// huge entry keeps writing long after Cancel returns, and a second instance acquiring the
    /// lock in that window extracts into the same game directory.
    ///
    /// Safe at any time and any number of times. Never completes the channel once the queue has
    /// run — <see cref="RunAsync"/>'s finally owns that. A queue that has been run must get here:
    /// this is the only thing that releases the linked source.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            // Required, not tidiness: _loop is never nulled, so a second pass would Cancel the
            // source the first disposed. The await makes this the same promise the first caller
            // got — without it, two concurrent disposers would tell the loser "done" while the
            // pack lock is still held. Guarded on _started, which the never-run branch leaves
            // with _finished uncompleted forever.
            if (Volatile.Read(ref _started) == 1) await _finished.Task;
            return;
        }

        // Not merely _completed: this clears the pause too, which is what the degraded path below
        // relies on when _loop is null and there is no cancellation to fall back on.
        StopAcceptingWork();

        // Read before _started: re-reading later could hand back a source a racing RunAsync is
        // still using. A disposal landing in the gap sees null, and _completed plus the permit
        // below still bring the loop down by draining.
        var loop = Volatile.Read(ref _loop);

        loop?.Cancel();
        _signal.Release();

        if (Volatile.Read(ref _started) == 1)
        {
            await _finished.Task;

            // Re-read only now: a disposal that saw null raced the assignment, and RunAsync's
            // finally has since run, so nothing still reads that token.
            loop = Volatile.Read(ref _loop);
        }
        else
        {
            // Never run, so RunAsync's finally will never close the channel and a consumer already
            // in `await foreach` would wait on it forever.
            _updates.Writer.TryComplete();
        }

        loop?.Dispose();

        // _signal is deliberately NOT disposed: a control arriving after disposal would throw out
        // of a method contracted to be safe at any time, and it holds no unmanaged resource.
    }
}
