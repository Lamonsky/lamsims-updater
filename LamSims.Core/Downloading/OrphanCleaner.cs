using System;
using System.Collections.Generic;
using System.IO;

namespace LamSims.Core.Downloading;

/// <summary>
/// Startup cleanup of partial downloads for packs no longer in the catalog, and of digest
/// records that describe an archive which is no longer there. Unlocker asset temps are swept by
/// <see cref="CleanAssetTemps"/>, which has a narrower safe moment and so is called separately.
/// Quarantined archives (.zip.bad) are never deleted automatically. They are the evidence for
/// a checksum failure and are removed only on explicit user action.
/// </summary>
public sealed class OrphanCleaner
{
    private readonly DownloadPaths _paths;

    public OrphanCleaner(DownloadPaths paths) => _paths = paths;

    public IReadOnlyList<string> CleanOrphans(IReadOnlySet<string> knownCodes)
    {
        if (!Directory.Exists(_paths.Root))
            return Array.Empty<string>();

        var deleted = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_paths.Root))
        {
            var name = Path.GetFileName(file);

            // Longest suffix first: '.part' would otherwise swallow '.part.json'. A code cannot
            // contain a path separator and no suffix is a prefix of another, so the match is
            // unambiguous.
            string code;
            var widowed = false;

            if (name.EndsWith(".part.json", StringComparison.Ordinal))
            {
                code = name[..^".part.json".Length];
            }
            else if (name.EndsWith(".zip.json", StringComparison.Ordinal))
            {
                code = name[..^".zip.json".Length];
            }
            else if (name.EndsWith(".part", StringComparison.Ordinal))
            {
                code = name[..^".part".Length];
            }
            else
            {
                continue;
            }

            // A file that is nothing but a suffix names no pack, and DownloadPaths refuses a
            // blank code rather than composing a path out of one.
            if (code.Length == 0)
                continue;

            if (name.EndsWith(".zip.json", StringComparison.Ordinal))
            {
                // A record describing an archive that is no longer there vouches for nothing.
                // Unlike a partial download, it is worth sweeping even for a known code.
                widowed = !File.Exists(_paths.ArchiveFile(code));
            }

            if (knownCodes.Contains(code) && !widowed)
                continue;

            Delete(file, deleted);
        }

        return deleted;
    }

    /// <summary>
    /// Unlocker asset temps only, and separate from <see cref="CleanOrphans"/> because it is not
    /// safe at the same moments. A temp is named for its asset and a random string, never for a
    /// pack, so no set of known codes can say which are live — where a <c>.part</c> file for a
    /// listed pack is protected by exactly that. Deleting a live one costs the fetch that owns it:
    /// on Linux the unlink succeeds, the writer carries on into an unlinked inode, and the read
    /// back fails with a name that no longer exists.
    ///
    /// So this is called once, at startup, before the shell can reach an install command — which
    /// makes it safe against this process. It is NOT safe against a second instance pointed at the
    /// same download directory and fetching at that moment; that install fails with a confusing
    /// message and mutates nothing, and closing the gap needs an owner claim the temp does not
    /// carry.
    /// </summary>
    public IReadOnlyList<string> CleanAssetTemps()
    {
        if (!Directory.Exists(_paths.Root))
            return Array.Empty<string>();

        var deleted = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_paths.Root, "*.incoming"))
            Delete(file, deleted);

        return deleted;
    }

    private static void Delete(string file, List<string> deleted)
    {
        try
        {
            File.Delete(file);
            deleted.Add(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file locked by another instance, or one we lack permission to remove, is left
            // alone; the next run retries it. UnauthorizedAccessException does not derive from
            // IOException, so it needs naming explicitly.
        }
    }
}
