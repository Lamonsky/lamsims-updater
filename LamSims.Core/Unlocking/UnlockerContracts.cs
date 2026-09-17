using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LamSims.Core.Unlocking;

public enum ClientKind { EaApp, Origin }

public enum UnlockerState { NotInstalled, Installed, Unknown }

public enum EnvironmentSource { Wine, Steam, Lutris, Heroic, Bottles }

/// <summary>
/// Where a target lives, when the client name alone cannot say. Structured rather than a display
/// string so a list can group or sort by source without parsing its own labels apart.
/// </summary>
/// <param name="Flatpak">
/// A launcher's native and Flatpak installs produce prefixes whose source and detail are
/// identical, so this is what separates their rows.
/// </param>
public sealed record TargetEnvironment(EnvironmentSource Source, string Detail, bool Flatpak = false)
{
    public string Describe() =>
        Flatpak ? $"{Source} (Flatpak), {Detail}" : $"{Source}, {Detail}";
}

/// <summary>
/// One place the unlocker can be installed. A list because a backend may find several: one per
/// Wine prefix for instance, and the EA-client backend returns one per installed EA client.
/// </summary>
public sealed record UnlockerTarget(string BackendId, ClientKind Client, string ClientPath,
                                   string DisplayName, string? PrefixPath = null,
                                   TargetEnvironment? Environment = null);

/// <summary>
/// Carries no version and no Outdated state, because the unlocker exposes nothing to detect a
/// version from. Installed means version.dll is present.
/// </summary>
public sealed record UnlockerStatus(UnlockerState State, string? Detail);

/// <param name="Step">The step about to run, for display.</param>
/// <param name="Completed">Steps already finished. Zero on the first report.</param>
/// <param name="Total">Steps this run will attempt, fixed per ClientKind and operation.</param>
public sealed record UnlockerProgress(string Step, int Completed, int Total);

/// <summary>
/// Warnings sit beside a success because several steps are non-critical; collapsing those into
/// failure would refuse installs that in fact worked. PackQueue carries Warnings the same way.
/// </summary>
public sealed record UnlockerResult(bool Success, string? Error = null,
                                   bool RequiresElevation = false,
                                   IReadOnlyList<string>? Warnings = null)
{
    public static UnlockerResult Ok(IReadOnlyList<string>? warnings = null) => new(true, Warnings: warnings);
    public static UnlockerResult Fail(string error, IReadOnlyList<string>? warnings = null) =>
        new(false, error, Warnings: warnings);
    public static UnlockerResult NeedsElevation() =>
        new(false, "Administrator rights are required.", RequiresElevation: true);
}

public interface IUnlockerBackend
{
    string Id { get; }
    bool IsSupported { get; }

    Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct);
    Task<UnlockerStatus> GetStatusAsync(UnlockerTarget target, CancellationToken ct);
    Task<UnlockerResult> InstallAsync(UnlockerTarget target, IUnlockerAssetSource assets,
                                      IProgress<UnlockerProgress> progress, CancellationToken ct);
    Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
                                     IProgress<UnlockerProgress> progress, CancellationToken ct);
}

public interface IUnlockerAssetSource
{
    /// <summary>
    /// The verified DLL's BYTES, not a path to them. The caller writes these under elevation seconds
    /// after this returns, and the cache they came from sits in a download directory the user can
    /// point anywhere, so handing back a path would leave a window in which the file that was
    /// checked and the file that gets installed are not the same file.
    /// </summary>
    Task<ReadOnlyMemory<byte>> GetDllAsync(ClientKind client, CancellationToken ct);
}

public sealed class UnlockerAssetMismatchException(string url, string expected, string actual)
    : Exception($"The DLL at '{url}' does not match the digest this build pins. " +
                $"Expected {expected}, got {actual}. Upstream may have replaced the release asset.")
{
    public string Expected { get; } = expected;
}
