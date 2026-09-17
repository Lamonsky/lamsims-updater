using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LamSims.Core;

/// <summary>
/// The two rules every catalog-to-filesystem comparison follows. They govern NAMES, not the
/// identity of a directory itself — see <see cref="DirectoryKey"/>, which keeps the second out.
///
/// NFC first, because an APFS volume returns the decomposed form of a name the catalog carries
/// composed, and an installed "Sims 4 Café" would read as absent forever. Then OrdinalIgnoreCase,
/// because Windows-written pack directories are routinely read through Wine or a shared mount.
/// Canonical equivalence beyond NFC/NFD is out of scope.
/// </summary>
internal static class PathIdentity
{
    /// <summary>NFC where the runtime can produce it, the original where it cannot: a Linux name
    /// is an arbitrary byte sequence, so it can be invalid Unicode, which Normalize throws on. A
    /// damaged name must degrade to an ordinal comparison, never take a scan down.</summary>
    public static string Normalize(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>The absolute, separator-trimmed, NFC form, or null when the value cannot be
    /// resolved. Null propagates as "matches nothing" rather than throwing, because one side of
    /// every comparison is a string read off disk.</summary>
    public static string? Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Normalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A short, filesystem-safe name for a directory, for the file holding its records. Hashed,
    /// since a path cannot be a directory name; the record still holds the path in full and every
    /// reader re-checks it, so a collision cannot make a record vouch for the wrong directory.
    ///
    /// Case is SIGNIFICANT here, unlike the rule this class applies to names within a directory:
    /// on Linux "/games/Prefix" and "/games/prefix" are two directories. Folding them cost the
    /// first its record outright — the second install overwrote it, and the first's removal then
    /// found nothing to undo. The reverse cost on a case-insensitive filesystem is one directory
    /// keying twice: a miss, never a merge, and it recovers by rescanning.
    /// </summary>
    public static string DirectoryKey(string gameDirectory)
    {
        var canonical = Canonical(gameDirectory) ?? gameDirectory;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
