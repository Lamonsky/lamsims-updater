using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LamSims.Core.Catalogs;
using LamSims.Core.Installing;

namespace LamSims.Core.Scanning;

/// <summary>
/// What is known about a pack on disk, ordered worst to best so a caller can sort by rank.
/// <see cref="InstalledUnverified"/> is every install this tool did not perform: one made by
/// another tool, one from before the journal existed, or one whose marker was lost with the
/// config directory. It belongs to the installed family and is never a warning.
/// </summary>
public enum PackInstallState { NotInstalled, Partial, InstalledUnverified, Installed }

/// <summary>One pack's state. Empty <see cref="MissingDirs"/> with
/// <see cref="PackInstallState.Partial"/> is the contract for "started and never finished".
/// <see cref="InstalledSha256"/> and <see cref="InstalledUtc"/> are set only for Installed; the
/// digest is what lets a caller derive "update available" with no state of its own.</summary>
public sealed record PackScanResult(
    string Code,
    PackInstallState State,
    IReadOnlyList<string> MissingDirs,
    string? InstalledSha256,
    DateTimeOffset? InstalledUtc);

/// <summary>The outcome of a scan. <see cref="GameDirectoryReadable"/> is false only when the
/// directory is absent or unenumerable, never merely empty — every pack reads NotInstalled either
/// way, so without the flag a caller cannot tell the two apart.</summary>
public sealed record ScanResult(bool GameDirectoryReadable, IReadOnlyList<PackScanResult> Packs);

/// <summary>
/// Detects installed packs by comparing directory NAMES under the game directory against each
/// pack's install directories, then consulting the journal for those whose directories are all
/// present. Names rather than a path substring, so a game at <c>D:\SP20\Sims 4</c> does not read
/// as having SP20 installed.
///
/// Directories are ground truth for absence, the journal for completion. Neither alone suffices: a
/// cancelled install leaves the directory it started, and a marker outlives what it describes.
/// </summary>
public static class InstallScanner
{
    /// <summary>
    /// Pure and total: never writes a marker, never throws on one's content, and never
    /// consults markers at all when the game directory could not be read, so state cannot
    /// claim a presence the scan could not confirm.
    /// </summary>
    /// <param name="markers">Markers for <paramref name="gameDirectory"/>, keyed by pack code
    /// case-insensitively — an ordinal comparer would miss markers over nothing but case.</param>
    public static ScanResult Scan(
        string gameDirectory,
        IEnumerable<PackEntry> packs,
        IReadOnlyDictionary<string, InstallMarker> markers)
    {
        // Case-insensitive: Windows-written pack directories are routinely read from a
        // case-sensitive filesystem through Wine. NFC too, because a volume can return a name in a
        // different normal form than the catalog carries; see PathIdentity.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readable = false;

        if (Directory.Exists(gameDirectory))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(gameDirectory))
                    present.Add(PathIdentity.Normalize(Path.GetFileName(directory)!));

                readable = true;
            }
            catch (IOException)
            {
                // Unreadable or deleted mid-enumeration: treated as absent, which keeps Scan total
                // and covers a game directory on an intermittent mount. EnumerateDirectories is
                // lazy, so clearing is what stops a partial listing reporting some packs
                // Installed.
                present.Clear();
            }
            catch (UnauthorizedAccessException)
            {
                // Same as IOException: the directory exists but cannot be read.
                present.Clear();
            }
        }

        var results = new List<PackScanResult>();

        // Canonicalised once rather than inside Applicable: every pack in the catalog would
        // otherwise re-run Path.GetFullPath and an NFC pass over the same scanned directory.
        var canonicalGameDirectory = PathIdentity.Canonical(gameDirectory);

        foreach (var pack in packs)
        {
            var missing = pack.InstallDirs
                .Where(d => !present.Contains(PathIdentity.Normalize(d)))
                .ToArray();

            // Checked before the "nothing missing" case so a pack with no install directories
            // reads NotInstalled rather than installed.
            if (missing.Length == pack.InstallDirs.Count)
            {
                results.Add(new PackScanResult(pack.Code, PackInstallState.NotInstalled, missing, null, null));
                continue;
            }

            if (missing.Length > 0)
            {
                results.Add(new PackScanResult(pack.Code, PackInstallState.Partial, missing, null, null));
                continue;
            }

            var marker = Applicable(markers, canonicalGameDirectory, pack.Code);

            if (marker is null)
            {
                results.Add(new PackScanResult(pack.Code, PackInstallState.InstalledUnverified, missing, null, null));
            }
            else if (marker.Status == InstallMarkerStatus.Installed)
            {
                results.Add(new PackScanResult(
                    pack.Code, PackInstallState.Installed, missing, marker.ArchiveSha256, marker.UpdatedUtc));
            }
            else
            {
                // Every directory present and the journal still open: the extraction was
                // interrupted. Empty MissingDirs is how a caller tells this apart from the
                // ordinary subset case.
                results.Add(new PackScanResult(pack.Code, PackInstallState.Partial, missing, null, null));
            }
        }

        return new ScanResult(readable, results);
    }

    /// <summary>The marker for <paramref name="code"/>, only if it vouches for this game
    /// directory. Re-checking the recorded path is what stops a hand-copied marker, or one whose
    /// directory was renamed, from applying.</summary>
    /// <param name="canonicalGameDirectory">Already canonicalised by the caller. Null means "does
    /// not apply" rather than throwing or matching a marker that is also unresolvable.</param>
    private static InstallMarker? Applicable(
        IReadOnlyDictionary<string, InstallMarker> markers, string? canonicalGameDirectory, string code)
    {
        if (!markers.TryGetValue(code, out var marker)) return null;
        if (canonicalGameDirectory is null) return null;

        var markerDirectory = PathIdentity.Canonical(marker.GameDirectory);

        return string.Equals(marker.Code, code, StringComparison.OrdinalIgnoreCase)
               && markerDirectory is not null
               && string.Equals(canonicalGameDirectory, markerDirectory, StringComparison.OrdinalIgnoreCase)
            ? marker
            : null;
    }
}
