using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Unlocking;

namespace LamSims.App.Tests;

/// <summary>
/// An App-side IUnlockerHost. LamSims.Core.Tests is not referenced from this project, so its
/// FakeUnlockerHost is unreachable; FakeDelayProvider and TestFileServer are duplicated per project
/// for the same reason.
/// </summary>
/// <param name="calls">
/// An externally owned log, mirroring RecordingQueue's own constructor, so a relaunch test can
/// interleave this host's "Relaunch" with the queue's "CancelAll"/"Dispose" in one ordered list;
/// shutdown's ordering rule has no other observable.
/// </param>
public sealed class FakeUnlockerHost(List<string>? calls = null) : IUnlockerHost
{
    public bool IsAvailable { get; set; } = true;
    public bool IsElevated { get; set; } = true;
    public bool RelaunchAccepted { get; set; } = true;
    public List<string> Calls { get; } = calls ?? [];

    public bool TryRelaunchElevated()
    {
        Calls.Add("Relaunch");
        return RelaunchAccepted;
    }

    public string? ReadClientPath(ClientRegistryKey key) => null;
    public AutostartValue? ReadAutostartValue(string name) => null;
    public void WriteAutostartValue(string name, AutostartValue value) { }
    public void RemoveAutostartValue(string name) { }
    public IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames) => [];
    public IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                                   TimeSpan perProcessTimeout) => [];
    public void DeleteScheduledTask(string name) => Calls.Add($"DeleteTask:{name}");
}

/// <summary>
/// Never called: the test hosts register no backend, so nothing can ask for a DLL. The four bytes
/// are an MZ header, so anything that did reach it receives something shaped like a PE image rather
/// than an empty buffer that a length check would silently accept.
/// </summary>
public sealed class StubUnlockerAssets : IUnlockerAssetSource
{
    public Task<ReadOnlyMemory<byte>> GetDllAsync(ClientKind client, CancellationToken ct) =>
        Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
}

/// <summary>
/// Drives a real <see cref="UnlockerService"/> rather than faking the service itself, so the
/// service's id routing stays exercised by these tests.
/// </summary>
public sealed class RecordingUnlockerBackend(params UnlockerTarget[] targets) : IUnlockerBackend
{
    public string Id => "test-backend";
    public bool IsSupported { get; set; } = true;
    public List<string> Calls { get; } = [];
    public UnlockerResult NextResult { get; set; } = UnlockerResult.Ok();
    public UnlockerState State { get; set; } = UnlockerState.NotInstalled;
    public List<UnlockerProgress> ToReport { get; } = [];

    /// <summary>The managed thread InstallAsync/RemoveAsync ran on, so a test can assert the
    /// operation left the caller's thread.</summary>
    public int OperationThreadId { get; private set; }

    /// <summary>
    /// Set to keep an operation in flight, the way a slow filesystem step would. Without it a test
    /// about IsBusy DURING the operation can never observe it: the fake would return before anyone
    /// looked, the same reason StubQueue.HoldDispose exists.
    /// </summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>
    /// Observed at the very start of an operation, before ToReport is replayed, so a test can see
    /// what the view model looked like as this target began rather than after its own progress
    /// has landed.
    /// </summary>
    public Action<UnlockerTarget>? OnEnter { get; set; }

    /// <summary>Per target, for a batch whose targets must not all answer the same way.</summary>
    public Func<UnlockerTarget, UnlockerResult>? ResultFor { get; set; }

    /// <summary>
    /// Set to keep detection in flight, mirroring <see cref="Hold"/> for install/remove. Without it
    /// a test about two overlapping detections can never observe "during": DetectTargetsAsync would
    /// already have returned before a second call could interleave with the first.
    /// </summary>
    public TaskCompletionSource? HoldDetect { get; set; }

    /// <summary>The managed thread DetectTargetsAsync ran on, mirroring OperationThreadId, so a
    /// test can assert detection left the caller's thread too.</summary>
    public int DetectThreadId { get; private set; }

    /// <summary>
    /// Every managed thread GetStatusAsync ran on, in call order. The batch reads a row's status
    /// after that row's operation, on a path OperationThreadId cannot see, and on both real
    /// backends that read is synchronous work — so an unhopped call runs it on the UI thread. A
    /// list rather than one field because a refresh reads statuses too: a single field would
    /// already hold a pool thread from the refresh, and an assertion against it would hold whether
    /// or not the batch hopped.
    /// </summary>
    public List<int> StatusThreadIds { get; } = [];

    /// <summary>
    /// Set to make a target THROW rather than return a failed result. A returned failure and a
    /// thrown one reach the batch loop by different paths, and only the thrown one can abandon it.
    /// </summary>
    public Func<UnlockerTarget, Exception?>? ThrowFor { get; set; }

    public async Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct)
    {
        DetectThreadId = Environment.CurrentManagedThreadId;

        if (HoldDetect is not null) await HoldDetect.Task;

        return targets;
    }

    public Task<UnlockerStatus> GetStatusAsync(UnlockerTarget t, CancellationToken ct)
    {
        StatusThreadIds.Add(Environment.CurrentManagedThreadId);

        return Task.FromResult(new UnlockerStatus(State, null));
    }

    public async Task<UnlockerResult> InstallAsync(UnlockerTarget t, IUnlockerAssetSource a,
                                           IProgress<UnlockerProgress> p, CancellationToken ct)
    {
        OperationThreadId = Environment.CurrentManagedThreadId;

        // The target's path, not just the verb: a row bound to its neighbour's data context calls the
        // right method for the wrong client, and only the path reveals it.
        Calls.Add($"Install:{t.ClientPath}");
        OnEnter?.Invoke(t);
        foreach (var report in ToReport) p.Report(report);

        if (Hold is not null) await Hold.Task;
        if (ThrowFor?.Invoke(t) is { } fault) throw fault;

        return ResultFor?.Invoke(t) ?? NextResult;
    }

    public async Task<UnlockerResult> RemoveAsync(UnlockerTarget t, IProgress<UnlockerProgress> p,
                                          CancellationToken ct)
    {
        OperationThreadId = Environment.CurrentManagedThreadId;

        Calls.Add($"Remove:{t.ClientPath}");
        OnEnter?.Invoke(t);

        if (Hold is not null) await Hold.Task;
        if (ThrowFor?.Invoke(t) is { } fault) throw fault;

        return ResultFor?.Invoke(t) ?? NextResult;
    }
}
