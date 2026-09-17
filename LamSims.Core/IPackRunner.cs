using System;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;

namespace LamSims.Core;

/// <summary>
/// Which part of one pack's run is under way. Reported by <see cref="IPackRunner"/> because
/// none of it is derivable from the progress streams: a verify emits nothing at all, and an
/// archive already vouched for by its digest record skips verification entirely.
/// </summary>
public enum PackPhase { Verifying, Downloading, Installing }

/// <summary>
/// One pack, end to end. Exists so a queue can be tested against an implementation whose
/// timing it controls; <see cref="PackWorkflow"/> is the real one.
/// </summary>
public interface IPackRunner
{
    Task<PackWorkflowResult> RunAsync(
        PackEntry pack,
        string gameDirectory,
        IProgress<PackPhase>? phase,
        IProgress<DownloadProgress>? downloadProgress,
        IProgress<InstallProgress>? installProgress,
        CancellationToken ct);
}
