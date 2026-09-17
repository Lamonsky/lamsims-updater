using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Logging;

namespace LamSims.Core.Installing;

public sealed class ZipSlipException : IOException
{
    public ZipSlipException(string entryName)
        : base($"Archive entry '{entryName}' resolves outside the game directory.")
    {
    }
}

public enum InstallOutcome { Installed, InsufficientSpace, Cancelled, Failed }

/// <summary>The outcome of an install. <see cref="Warnings"/> carries what went wrong without
/// failing it, and is never null so a caller can bind it directly.</summary>
public sealed record InstallResult(
    InstallOutcome Outcome, int EntriesWritten, string? Error, IReadOnlyList<string> Warnings)
{
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    public static InstallResult Installed(int entriesWritten, IReadOnlyList<string>? warnings = null) =>
        new(InstallOutcome.Installed, entriesWritten, null, warnings ?? None);

    public static InstallResult InsufficientSpace(string error) =>
        new(InstallOutcome.InsufficientSpace, 0, error, None);

    public static InstallResult Cancelled(int entriesWritten) =>
        new(InstallOutcome.Cancelled, entriesWritten, null, None);

    public static InstallResult Failed(string error, int entriesWritten = 0) =>
        new(InstallOutcome.Failed, entriesWritten, error, None);
}

public sealed record InstallProgress(string Code, long BytesWritten, long TotalBytes, string CurrentEntry);

/// <summary>
/// Streams archive entries straight into the game directory, so an install needs the pack's size
/// free rather than three times it.
///
/// The zip-slip guard is LEXICAL: <see cref="Path.GetFullPath(string)"/> does not resolve
/// symlinks, so a symlink already inside the game directory and pointing out of it passes the
/// prefix check and the entry is written through it. This defends against a hostile archive, not
/// against a game directory that already contains a hostile symlink.
///
/// Extraction is journalled — a marker written durably before the first entry and rewritten as
/// complete after the last. A marker still reading "installing" is what makes an interruption
/// visible, since a cancelled extraction leaves a directory that looks filled.
///
/// A failed install cleans up neither the marker nor the files: entries overwrite existing files,
/// including shared roots such as Delta/, so a rollback without pre-images would destroy another
/// pack's current state.
/// </summary>
public sealed class ZipInstaller
{
    private const int BufferSize = 1024 * 1024;

    private readonly InstallStateStore _state;
    private readonly Func<string, long> _availableBytes;
    private readonly Action<InstallProgress>? _entryWritten;
    private readonly ILogSink _log;

    /// <param name="availableBytes">Free bytes on the volume. A test supplies its own, because the
    /// sizes that make the space checks fire cannot be produced on a real volume.</param>
    /// <param name="entryWritten">Called on the extracting thread as each entry lands, before the
    /// next cancellation check, so a test can act ordered against the extract rather than racing
    /// a write.</param>
    public ZipInstaller(
        InstallStateStore state,
        Func<string, long>? availableBytes = null,
        Action<InstallProgress>? entryWritten = null,
        ILogSink? log = null)
    {
        _state = state;
        _availableBytes = availableBytes ?? DiskSpace.GetAvailableBytes;
        _entryWritten = entryWritten;
        _log = log ?? NullLogSink.Instance;
    }

    private void EnsureSpace(string path, long requiredBytes)
    {
        var available = _availableBytes(path);
        if (available < requiredBytes)
            throw new InsufficientDiskSpaceException(path, requiredBytes, available);
    }

    /// <summary>
    /// Reports cancellation as a <c>Cancelled</c> outcome rather than throwing. The caller must
    /// already have verified <paramref name="archivePath"/> against <paramref name="pack"/>'s
    /// digest: the marker records that digest as the identity of what was installed, without
    /// re-checking the bytes.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        PackEntry pack, string archivePath, string gameDirectory,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var written = 0;

        if (string.IsNullOrWhiteSpace(gameDirectory))
            return InstallResult.Failed("The 'gameDirectory' argument must not be blank.");

        // The game directory already exists by definition. Creating whatever was supplied would
        // materialise a typo and extract gigabytes into it.
        if (!Directory.Exists(gameDirectory))
            return InstallResult.Failed($"The game directory '{gameDirectory}' does not exist.");

        try
        {
            _log.Write(LogLine.Info($"Installing into {gameDirectory}", pack.Code));

            EnsureSpace(gameDirectory, pack.RequiredInstallBytes);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));

            // A root keeps its trailing separator, so appending a second gives a prefix ('//',
            // 'D:\\') no destination starts with, and every entry reads as an escape.
            var inside = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            // 'root' rather than the caller's string: the recorded directory is what the
            // scanner compares against, so it is stored already resolved.
            var marker = new InstallMarker(
                InstallMarker.CurrentSchemaVersion, pack.Code, root, pack.Sha256,
                InstallMarkerStatus.Installing, DateTimeOffset.UtcNow);

            // Closed before the delete below: opened FileShare.Read without FileShare.Delete, so
            // on Windows deleting it while open is a sharing violation.
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                // Every destination is resolved before a single byte is written. A hostile entry
                // found halfway through would already have put files outside the game directory,
                // and resolution reads only the central directory, so it costs nothing.
                var planned = new List<(ZipArchiveEntry Entry, string Destination)>(archive.Entries.Count);
                var totalBytes = 0L;

                foreach (var entry in archive.Entries)
                {
                    string destination;
                    try
                    {
                        destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException)
                    {
                        // A name the path APIs reject is reported the same way a bad write is:
                        // the entry is named rather than the process crashing on a hostile archive.
                        throw new IOException($"Archive entry '{entry.FullName}' has a name that is not a valid path: {e.Message}", e);
                    }

                    if (destination != root
                        && !destination.StartsWith(inside, StringComparison.Ordinal))
                    {
                        throw new ZipSlipException(entry.FullName);
                    }

                    planned.Add((entry, destination));
                    totalBytes += entry.Length;
                }

                // installedSize is optional, and the fallback is the ARCHIVE size — a large
                // underestimate. The central directory has just given the real figure, and a
                // refusal here still costs nothing.
                EnsureSpace(gameDirectory, totalBytes);

                // Written after every destination has been resolved and checked, and before a
                // single byte lands: everything that can fail without leaving a trace has
                // already failed, so a refusal here cannot demote a pack that is installed.
                try
                {
                    await _state.SaveAsync(marker, ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Without a journal, a cancel partway through would be indistinguishable
                    // from a complete install for the rest of the installation's life.
                    return InstallResult.Failed(
                        $"The install journal for '{pack.Code}' under '{_state.Root}' could not be written: {e.Message}");
                }

                var bytes = 0L;

                foreach (var (entry, destination) in planned)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        // A directory entry carries a trailing separator and an empty Name.
                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                        await using var source = entry.Open();
                        await using var target = new FileStream(
                            destination, FileMode.Create, FileAccess.Write, FileShare.None,
                            BufferSize, FileOptions.Asynchronous);

                        await source.CopyToAsync(target, ct);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        // The entry is named in the message, so a report says which one failed
                        // and whether it was creating its parent directory or writing its bytes.
                        throw new IOException(
                            $"Entry '{entry.FullName}' could not be written to '{destination}': {e.Message}", e);
                    }

                    written++;
                    bytes += entry.Length;

                    var landed = new InstallProgress(pack.Code, bytes, totalBytes, entry.FullName);
                    Report(progress, landed);

                    if (_entryWritten is { } entryWritten)
                    {
                        try
                        {
                            entryWritten(landed);
                        }
                        catch (Exception e)
                        {
                            // Rethrown as IOException so it lands in this method's filter and comes
                            // back as a failed install rather than escaping and breaking the
                            // always-returns-an-InstallResult guarantee.
                            throw new IOException(
                                $"The entry-written callback failed after '{entry.FullName}': {e.Message}", e);
                        }
                    }
                }
            }

            _log.Write(LogLine.Info($"Installed {pack.Code}, {written} entries", pack.Code));

            var warnings = new List<string>();

            try
            {
                // CancellationToken.None: the install has already succeeded, and a racing
                // cancel must not leave the journal claiming the extraction is still running.
                await _state.SaveAsync(
                    marker with { Status = InstallMarkerStatus.Installed, UpdatedUtc = DateTimeOffset.UtcNow },
                    CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not a failure: every entry is on disk. But the journal now disagrees with the
                // filesystem and the next scan reads this pack as interrupted, so say so.
                warnings.Add(
                    $"'{pack.Code}' installed, but its journal under '{_state.Root}' could not be updated: {e.Message}. "
                    + "The pack will show as incomplete until it is installed again.");
            }

            // Only now: a failed install keeps the verified archive so a retry needs no download.
            // Its own try/catch, because a second process holding the archive open must not turn
            // a completed install into a failure whose retry re-extracts everything.
            TryDeleteArchive(archivePath);
            return InstallResult.Installed(written, warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return InstallResult.Cancelled(written);
        }
        catch (InsufficientDiskSpaceException e)
        {
            _log.Write(LogLine.Error($"Install failed: {e.Message}", pack.Code));
            return InstallResult.InsufficientSpace(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or InvalidDataException or ArgumentException)
        {
            // ArgumentException covers the path APIs, which reject a name rather than failing
            // to use it: a UNC game directory on Windows reaches DriveInfo this way. This
            // method always returns an InstallResult.
            _log.Write(LogLine.Error($"Install failed: {e.Message}", pack.Code));
            return InstallResult.Failed(e.Message, written);
        }
    }

    private static void TryDeleteArchive(string archivePath)
    {
        try
        {
            File.Delete(archivePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reporting progress must not be able to fail an install that is otherwise fine.</summary>
    private static void Report(IProgress<InstallProgress>? progress, InstallProgress value)
    {
        if (progress is null) return;

        try
        {
            progress.Report(value);
        }
        catch (Exception)
        {
        }
    }
}
