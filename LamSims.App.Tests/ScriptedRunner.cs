using LamSims.Core.Catalogs;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;

namespace LamSims.App.Tests;

/// <summary>
/// An IPackRunner whose timing the test owns. Every call parks on a gate until the test
/// releases it, so a test can hold a pack in flight and assert what the shell does meanwhile.
/// </summary>
public sealed class ScriptedRunner : IPackRunner
{
    private readonly SemaphoreSlim _gate = new(0);

    public List<string> Started { get; } = new();

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<PackEntry, PackWorkflowResult> Result { get; set; } =
        _ => new PackWorkflowResult(PackStage.Done, null, InstallResult.Installed(1), Array.Empty<string>());

    public Action<PackEntry, IProgress<PackPhase>?, IProgress<DownloadProgress>?, IProgress<InstallProgress>?>? Script { get; set; }

    public void Release() => _gate.Release();

    public async Task<PackWorkflowResult> RunAsync(
        PackEntry pack, string gameDirectory,
        IProgress<PackPhase>? phase, IProgress<DownloadProgress>? downloadProgress,
        IProgress<InstallProgress>? installProgress, CancellationToken ct)
    {
        lock (Started) Started.Add(pack.Code);

        Script?.Invoke(pack, phase, downloadProgress, installProgress);
        Entered.TrySetResult();

        await _gate.WaitAsync(ct);

        return Result(pack);
    }
}
