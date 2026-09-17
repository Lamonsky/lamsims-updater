using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LamSims.Core.Unlocking;

/// <summary>
/// Presents the UI with one flat list of targets drawn from every supported backend, and routes an
/// operation back to the backend whose Id the target carries. It names no platform of its own.
/// </summary>
public sealed class UnlockerService(IEnumerable<IUnlockerBackend> backends)
{
    private readonly IUnlockerBackend[] _backends = backends.ToArray();

    public bool IsSupported => _backends.Any(b => b.IsSupported);

    /// <summary>
    /// The registered backend ids, so a caller can check that the real object graph registered
    /// the backends it expects.
    /// </summary>
    public IReadOnlyList<string> BackendIds => _backends.Select(b => b.Id).ToArray();

    public async Task<IReadOnlyList<UnlockerTarget>> DetectAllAsync(CancellationToken ct)
    {
        var all = new List<UnlockerTarget>();

        // Only supported backends are asked. Off Windows the EA-client backend's host reports
        // unavailable, and calling into it would reach registry members that cannot work there.
        foreach (var backend in _backends.Where(b => b.IsSupported))
            all.AddRange(await backend.DetectTargetsAsync(ct));

        return all;
    }

    public Task<UnlockerStatus> GetStatusAsync(UnlockerTarget target, CancellationToken ct) =>
        For(target).GetStatusAsync(target, ct);

    public Task<UnlockerResult> InstallAsync(UnlockerTarget target, IUnlockerAssetSource assets,
                                            IProgress<UnlockerProgress> progress,
                                            CancellationToken ct) =>
        For(target).InstallAsync(target, assets, progress, ct);

    public Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
                                           IProgress<UnlockerProgress> progress,
                                           CancellationToken ct) =>
        For(target).RemoveAsync(target, progress, ct);

    /// <summary>
    /// By Id, never by position. Two backends can both offer an EA app target, so a positional
    /// lookup would act on the wrong machine.
    /// </summary>
    private IUnlockerBackend For(UnlockerTarget target) =>
        _backends.FirstOrDefault(b => string.Equals(b.Id, target.BackendId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"No unlocker backend with id '{target.BackendId}' is registered.");

}
