using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LamSims.Core.Downloading;

/// <summary>
/// The checksum gate. A match promotes the partial file to an archive; a mismatch quarantines
/// it as evidence. Re-downloading after a mismatch takes a user action, so a multi-gigabyte
/// transfer is never repeated unprompted.
/// </summary>
public sealed class ArchiveFinalizer
{
    private readonly DownloadPaths _paths;
    private readonly ArchiveDigestStore _digests;

    public ArchiveFinalizer(DownloadPaths paths)
    {
        _paths = paths;
        _digests = new ArchiveDigestStore(paths);
    }

    public async Task<DownloadResult> FinalizeAsync(
        string code, string expectedSha256, PartStateStore state, bool usedSingleStream, CancellationToken ct)
    {
        var partFile = _paths.PartFile(code);
        if (!File.Exists(partFile))
        {
            // The sidecar describes chunks inside a file that no longer exists. Leaving it
            // would let the next run preallocate a fresh, zero-filled part file and skip
            // those "completed" chunks, corruption the checksum catches only after the
            // whole archive transfers again.
            state.Delete();
            return DownloadResult.Failed($"Partial file '{partFile}' is missing.", usedSingleStream);
        }

        var actual = await Sha256Verifier.ComputeAsync(partFile, ct);

        if (string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            var archive = _paths.ArchiveFile(code);
            File.Move(partFile, archive, overwrite: true);

            // Statted after the rename, so the recorded identity is what a later run will see
            // rather than what the part file happened to look like.
            await _digests.RecordAsync(code, archive, actual);

            state.Delete();
            return DownloadResult.Completed(archive, actual, usedSingleStream);
        }

        var quarantine = _paths.QuarantineFile(code);
        File.Move(partFile, quarantine, overwrite: true);
        state.Delete();
        return DownloadResult.ChecksumMismatch(quarantine, expectedSha256, actual, usedSingleStream);
    }
}
