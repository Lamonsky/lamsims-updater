using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LamSims.Core.Unlocking.Wine;

/// <summary>
/// The two questions about running processes the Wine support has to ask, behind an interface so
/// every operation test drives a fake and nothing in the suite reads the real /proc.
/// </summary>
public interface IWineProcesses
{
    /// <summary>
    /// Whether anything is using this prefix. The registry write needs an idle prefix: wineserver
    /// holds the registry in memory and rewrites user.reg when it saves, so an edit written while a
    /// prefix is live is discarded by that flush.
    /// </summary>
    bool IsPrefixLive(string prefixRoot);

    /// <summary>
    /// Which of these client executables are running IN THIS PREFIX. Scoped to the prefix because
    /// the same client running in another one is not something the user can act on here.
    /// </summary>
    IReadOnlyList<string> RunningClients(string prefixRoot, IReadOnlyList<string> processNames);
}

/// <summary>
/// Reads /proc. Ordinary file and link reads throughout, so no platform attribute is needed and
/// CA1416 stays quiet; on a system with no /proc — macOS, which is out of scope —
/// every answer is "nothing running", which is UNVERIFIABLE there, not correct: a live wineserver
/// on such a system would go undetected and its next flush could discard a registry write in
/// progress.
///
/// A process is attributed to a prefix by three sources: a mapped file under it, a working
/// directory under it, or an open descriptor under it. Not by WINEPREFIX in
/// /proc/&lt;pid&gt;/environ, which is frequently UNSET — ~/.wine is the default — and would
/// report idle while a server is live, the one failure this check exists to prevent.
/// </summary>
public sealed class WineProcesses(string procRoot = "/proc") : IWineProcesses
{
    public bool IsPrefixLive(string prefixRoot)
    {
        var prefix = PathIdentity.Canonical(prefixRoot);

        return prefix is not null && Pids().Any(pid => Uses(pid, prefix));
    }

    public IReadOnlyList<string> RunningClients(string prefixRoot,
                                               IReadOnlyList<string> processNames)
    {
        var prefix = PathIdentity.Canonical(prefixRoot);
        if (prefix is null) return [];

        var found = new List<string>();

        foreach (var pid in Pids())
        {
            var name = ExecutableName(pid);
            if (name is null) continue;

            var match = processNames.FirstOrDefault(
                n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                     || string.Equals($"{n}.exe", name, StringComparison.OrdinalIgnoreCase));

            if (match is null || found.Contains(match, StringComparer.OrdinalIgnoreCase)) continue;
            if (!Uses(pid, prefix)) continue;

            found.Add(match);
        }

        return found;
    }

    private IEnumerable<string> Pids()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(procRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // /proc holds self, thread-self and a few others. A non-numeric entry is not a process.
        return entries.Where(d => int.TryParse(Path.GetFileName(d), out _));
    }

    /// <summary>
    /// argv[0]'s last segment, across BOTH separators: a Wine process's command line can carry a
    /// Windows path, and splitting on '/' alone leaves the whole string and matches nothing. Read
    /// from cmdline rather than comm because comm is capped at 15 bytes, which truncates
    /// EABackgroundService.exe to EABackgroundSer.
    /// </summary>
    private string? ExecutableName(string pid)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(pid, "cmdline"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The process exited between the listing and the read, which is ordinary.
            return null;
        }

        var argv0 = text.Split('\0', 2)[0];

        return argv0.Length == 0 ? null : argv0.Split('/', '\\')[^1];
    }

    private bool Uses(string pid, string prefix)
    {
        if (Maps(pid).Any(path => Under(path, prefix))) return true;
        if (Link(Path.Combine(pid, "cwd")) is { } cwd && Under(cwd, prefix)) return true;

        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(pid, "fd")))
                if (Link(fd) is { } target && Under(target, prefix)) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another user's process, or one that exited. Not a fault.
        }

        return false;
    }

    private IEnumerable<string> Maps(string pid)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(pid, "maps"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // Six space-separated fields, the last of which is the path and is absent for anonymous
        // mappings. A path can contain spaces, so it is taken as the remainder rather than split.
        return lines.Select(PathField).Where(p => p is not null).Select(p => p!);
    }

    private static string? PathField(string line)
    {
        var at = 0;
        for (var field = 0; field < 5; field++)
        {
            at = line.IndexOf(' ', at);
            if (at < 0) return null;
            while (at < line.Length && line[at] == ' ') at++;
        }

        var path = line[at..].Trim();

        return path.StartsWith('/') ? path : null;
    }

    private static string? Link(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Full-segment containment after canonicalisation, never StartsWith on the raw strings:
    /// ~/.wine must not match ~/.wine-backup.
    /// </summary>
    private static bool Under(string path, string prefix)
    {
        var canonical = PathIdentity.Canonical(path);
        if (canonical is null) return false;

        if (string.Equals(canonical, prefix, StringComparison.Ordinal)) return true;

        return canonical.Length > prefix.Length
               && canonical.StartsWith(prefix, StringComparison.Ordinal)
               && canonical[prefix.Length] == Path.DirectorySeparatorChar;
    }
}
