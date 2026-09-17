using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Unlocking;

namespace LamSims.App.ViewTests;

/// <summary>
/// ViewTests' own copy: this project cannot see LamSims.App.Tests' FakeUnlockerHost, and the two
/// projects must stay off a shared reference because they sit on different xunit major versions.
/// FakeDelayProvider and TestFileServer carry the same duplication for the same reason.
/// </summary>
public sealed class FakeUnlockerHost : IUnlockerHost
{
    public bool IsAvailable { get; set; } = true;
    public bool IsElevated { get; set; } = true;
    public bool RelaunchAccepted { get; set; } = true;
    public List<string> Calls { get; } = [];

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
/// Never called: the view test host registers no backend, so nothing can ask for a DLL. The four
/// bytes are an MZ header, so anything that did reach it receives something shaped like a PE image
/// rather than an empty buffer that a length check would silently accept.
/// </summary>
public sealed class StubUnlockerAssets : IUnlockerAssetSource
{
    public Task<ReadOnlyMemory<byte>> GetDllAsync(ClientKind client, CancellationToken ct) =>
        Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
}

/// <summary>
/// ViewTests' own recording backend, for the same reason as <see cref="FakeUnlockerHost"/> above:
/// LamSims.App.Tests' RecordingUnlockerBackend is unreachable from this project. Drives a real
/// <see cref="UnlockerService"/> rather than faking the service itself, so the row-to-target routing
/// UnlockerViewModelTests proves stays exercised through the actual XAML commands too.
/// </summary>
public sealed class RecordingUnlockerBackend : IUnlockerBackend
{
    public string Id => "test-backend";
    public bool IsSupported { get; set; } = true;

    /// <summary>Settable rather than fixed at construction, so a test can change what the next
    /// RefreshAsync detects without building a new backend.</summary>
    public IReadOnlyList<UnlockerTarget> Targets { get; set; } = [];

    /// <summary>The verb AND the target's ClientPath, never the verb alone: a row bound to its
    /// neighbour's data context would call the right method for the wrong client, and only the
    /// path reveals it.</summary>
    public List<string> Calls { get; } = [];

    public UnlockerResult NextResult { get; set; } = UnlockerResult.Ok();
    public UnlockerState State { get; set; } = UnlockerState.NotInstalled;

    /// <summary>Reported, in order, on every InstallAsync/RemoveAsync call: the backend's way of
    /// driving UnlockerViewModel's progress fields without a real multi-step operation.</summary>
    public List<UnlockerProgress> ToReport { get; } = [];

    public Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct) =>
        Task.FromResult(Targets);

    public Task<UnlockerStatus> GetStatusAsync(UnlockerTarget target, CancellationToken ct) =>
        Task.FromResult(new UnlockerStatus(State, null));

    public Task<UnlockerResult> InstallAsync(UnlockerTarget target, IUnlockerAssetSource assets,
        IProgress<UnlockerProgress> progress, CancellationToken ct)
    {
        Calls.Add($"Install:{target.ClientPath}");
        foreach (var report in ToReport) progress.Report(report);
        return Task.FromResult(NextResult);
    }

    public Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
        IProgress<UnlockerProgress> progress, CancellationToken ct)
    {
        Calls.Add($"Remove:{target.ClientPath}");
        foreach (var report in ToReport) progress.Report(report);
        return Task.FromResult(NextResult);
    }
}
