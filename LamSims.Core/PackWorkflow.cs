using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Logging;

namespace LamSims.Core;

/// <summary>How far a pack got. <see cref="Done"/> means downloaded, verified and installed.</summary>
public enum PackStage { Downloading, Installing, Done }

/// <summary>The outcome of one pack's run. <see cref="Warnings"/> carries what went wrong without
/// failing it — a quarantined archive, an unwritable journal. Never null, so a caller can bind it
/// directly.</summary>
public sealed record PackWorkflowResult(
    PackStage ReachedStage,
    DownloadResult? Download,
    InstallResult? Install,
    IReadOnlyList<string> Warnings);

/// <summary>One pack, end to end: fetch if absent, verify, install. Enforces no ordering of its
/// own — the caller must run packs one at a time, because the connection budget belongs to a single
/// archive and two calls would each claim all of it.</summary>
public sealed class PackWorkflow : IPackRunner
{
    /// <summary>What the archive already on disk is worth, if there is one.</summary>
    private enum ArchiveTrust { Trusted, NeedsDownload, Cancelled }

    private readonly SegmentedDownloader _downloader;
    private readonly ZipInstaller _installer;
    private readonly DownloadPaths _paths;
    private readonly ArchiveDigestStore _digests;
    private readonly IMirrorResolver _resolver;
    private readonly ILogSink _log;

    /// <param name="resolver">Turns the catalog's urls into fetchable ones. Defaults to the
    /// pass-through, so a caller with no share-hosted packs need not name one.</param>
    public PackWorkflow(
        SegmentedDownloader downloader, ZipInstaller installer, DownloadPaths paths,
        ILogSink? log = null, IMirrorResolver? resolver = null)
    {
        _downloader = downloader;
        _installer = installer;
        _paths = paths;
        _digests = new ArchiveDigestStore(paths);
        _resolver = resolver ?? PassThroughMirrorResolver.Instance;
        _log = log ?? NullLogSink.Instance;
    }

    public async Task<PackWorkflowResult> RunAsync(
        PackEntry pack,
        string gameDirectory,
        IProgress<PackPhase>? phase,
        IProgress<DownloadProgress>? downloadProgress,
        IProgress<InstallProgress>? installProgress,
        CancellationToken ct)
    {
        var archive = _paths.ArchiveFile(pack.Code);
        DownloadResult? download = null;
        var warnings = new List<string>();

        var trust = await ClassifyAsync(pack, archive, warnings, phase, ct);

        if (trust == ArchiveTrust.Cancelled)
            return new PackWorkflowResult(PackStage.Downloading, DownloadResult.Cancelled(), null, warnings);

        if (trust == ArchiveTrust.NeedsDownload)
        {
            Report(phase, PackPhase.Downloading);

            DownloadRequest request;
            try
            {
                // Only on the path that actually fetches: an archive already on disk and vouched
                // for must not put the run behind a third-party listing service. Result-shaped
                // like everything else here, since a share that has gone is a failure of this
                // pack rather than of the queue.
                request = await _resolver.ResolveAsync(pack.ToDownloadRequest(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new PackWorkflowResult(
                    PackStage.Downloading, DownloadResult.Cancelled(), null, warnings);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                _log.Write(LogLine.Error($"Could not resolve a download url: {e.Message}", pack.Code));

                return new PackWorkflowResult(
                    PackStage.Downloading, DownloadResult.Failed(e.Message), null, warnings);
            }

            download = await _downloader.DownloadAsync(request, downloadProgress, ct);

            if (download.Outcome != DownloadOutcome.Completed)
                return new PackWorkflowResult(PackStage.Downloading, download, null, warnings);

            archive = download.FilePath!;
        }

        Report(phase, PackPhase.Installing);
        var install = await _installer.InstallAsync(pack, archive, gameDirectory, installProgress, ct);

        // The installer deletes the archive it consumed; the record describes a file that is
        // no longer there, so it goes with it.
        if (install.Outcome == InstallOutcome.Installed)
            _digests.Delete(pack.Code);

        warnings.AddRange(install.Warnings);

        return new PackWorkflowResult(
            install.Outcome == InstallOutcome.Installed ? PackStage.Done : PackStage.Installing,
            download,
            install,
            warnings);
    }

    /// <summary>
    /// Whether the archive on disk can be installed without fetching it again. Length alone
    /// cannot skip hashing: the digest record is bound to the file's (length, last-write)
    /// identity, so a file swapped in underneath re-hashes rather than inheriting the earlier
    /// verification. A record that does not vouch means re-hash, NOT re-download. Never throws.
    /// </summary>
    private async Task<ArchiveTrust> ClassifyAsync(
        PackEntry pack, string archive, List<string> warnings, IProgress<PackPhase>? phase, CancellationToken ct)
    {
        // One FileInfo snapshot rather than a separate File.Exists check: orphan cleanup or a
        // second instance can remove the archive between the two calls, and FileInfo.Length
        // throws FileNotFoundException on a file that no longer exists, which would otherwise
        // escape this result-shaped method.
        var info = new FileInfo(archive);
        if (!info.Exists || info.Length != pack.Size)
            return ArchiveTrust.NeedsDownload;

        var record = _digests.TryLoad(pack.Code);

        if (record is not null
            && string.Equals(record.Sha256, pack.Sha256, StringComparison.OrdinalIgnoreCase)
            && record.Length == info.Length
            && record.LastWriteTimeUtc == new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
        {
            _log.Write(LogLine.Info(
                "Trusting the existing archive, its digest record still matches", pack.Code));
            return ArchiveTrust.Trusted;
        }

        string actual;
        try
        {
            _log.Write(LogLine.Info("Verifying the archive", pack.Code));
            Report(phase, PackPhase.Verifying);
            actual = await Sha256Verifier.ComputeAsync(archive, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ArchiveTrust.Cancelled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The archive vanished or became unreadable mid-hash. The download that follows
            // reports its own failure result-shaped; nothing escapes RunAsync from here.
            return ArchiveTrust.NeedsDownload;
        }

        if (!string.Equals(actual, pack.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"digest mismatch (expected {pack.Sha256[..8]}…, got {actual[..8]}…)";
            _log.Write(LogLine.Warning($"Archive quarantined: {reason}", pack.Code));
            Quarantine(pack.Code, archive, warnings);
            return ArchiveTrust.NeedsDownload;
        }

        await _digests.RecordAsync(pack.Code, archive, actual);

        return ArchiveTrust.Trusted;
    }

    /// <summary>Sets a wrong-digest archive aside rather than letting the next download overwrite
    /// it, as <see cref="ArchiveFinalizer"/> does. A multi-gigabyte file the user may have put
    /// there is not deleted over a hash disagreement, and the mismatch leaves something to look
    /// at. Its record goes too, since it described bytes no longer at that path.</summary>
    private void Quarantine(string code, string archive, List<string> warnings)
    {
        var quarantined = _paths.QuarantineFile(code);

        try
        {
            File.Move(archive, quarantined, overwrite: true);
            warnings.Add(
                $"'{code}': the archive on disk did not hash to the digest the catalog gives for this "
                + $"pack, so it was moved to '{quarantined}' instead of being deleted, and a fresh copy "
                + "is being downloaded.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The download's own promotion overwrites the archive in place, so a failure to
            // move it aside costs the evidence, not the correctness.
            warnings.Add(
                $"'{code}': the archive on disk did not hash to the digest the catalog gives for this "
                + $"pack, but it could not be moved aside ({e.Message}). It stays where it is and the "
                + "fresh download about to start will overwrite it, so the evidence is gone.");
        }

        _digests.Delete(code);
    }

    private static void Report(IProgress<PackPhase>? phase, PackPhase value)
    {
        try { phase?.Report(value); }
        catch (Exception) { }
    }
}
