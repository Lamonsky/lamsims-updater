using System;
using System.IO;
using LamSims.Core.Downloading;

namespace LamSims.Core.Queueing;

/// <summary>
/// An advisory lock over one pack code, held for that pack's whole run: verify, download and
/// install. A sequential queue orders one process against itself; this orders it against a
/// second instance, which would otherwise overwrite the same chunk sidecar, delete an archive
/// the other is still extracting, and share one connection budget between two transfers.
///
/// Nothing reaps a stale lock: the operating system closes the handle when a process dies,
/// however it died.
///
/// The lock file is deliberately left on disk when the lock is released, so its existence says
/// nothing about whether the lock is held; exclusion comes from the held handle alone. Deleting
/// it on release would let an acquirer that opened the path just before a release hold a lock on
/// an inode that no longer has a name, while the next acquirer creates a fresh inode at the same
/// path and holds it too. A stable path-to-inode mapping is what makes a second holder
/// impossible, for as long as nothing outside the application unlinks the file. A user or a
/// cleanup script that deletes a lock file mid-run reopens exactly that hole, since the holder
/// keeps its descriptor on the now-nameless inode while the next acquirer creates a fresh one.
/// That residue is inherent to POSIX advisory locking and is not fixable here.
/// </summary>
public sealed class PackLock : IDisposable
{
    private readonly FileStream _stream;

    private PackLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Null means another holder has it. Every other failure throws: reporting a read-only
    /// download root as "another instance holds this pack" would name a cause that does not
    /// exist.
    /// </summary>
    /// <remarks>
    /// The download root must already exist, so call <see cref="DownloadPaths.EnsureCreated"/>
    /// first. This does not create it, and a missing root throws
    /// <see cref="DirectoryNotFoundException"/> rather than reporting contention.
    /// </remarks>
    public static PackLock? TryAcquire(DownloadPaths paths, string code)
    {
        var path = paths.LockFile(code);

        try
        {
            return new PackLock(new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1));
        }
        // DirectoryNotFoundException and FileNotFoundException derive from IOException and are
        // not contention; the caller must create the root before acquiring. A generic
        // IOException is treated as contention because .NET surfaces no portable code that
        // distinguishes a sharing violation from a disk error, which errs toward retrying
        // rather than failing a pack outright.
        catch (IOException e) when (e is not DirectoryNotFoundException and not FileNotFoundException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
