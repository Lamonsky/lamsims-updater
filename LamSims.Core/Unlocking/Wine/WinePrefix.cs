using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LamSims.Core.Logging;

namespace LamSims.Core.Unlocking.Wine;

public enum WineArch { Win64, Win32 }

/// <summary>
/// One Wine prefix, opened and validated, and the owner of Windows-to-Linux path translation. On
/// Linux <c>Path.GetDirectoryName(@"C:\Program Files\EA\EADesktop.exe")</c> returns EMPTY —
/// <c>\</c> is not a separator — so a raw registry value finds nothing, silently, on every prefix.
/// </summary>
public sealed class WinePrefix
{
    private WinePrefix(string root, string driveC, WineArch arch, bool protonManaged,
                       TargetEnvironment environment, IReadOnlyList<string> userDirectories)
    {
        Root = root;
        DriveC = driveC;
        Arch = arch;
        ProtonManaged = protonManaged;
        Environment = environment;
        WindowsUserDirectories = userDirectories;
    }

    public string Root { get; }
    public string DriveC { get; }
    public WineArch Arch { get; }
    public bool ProtonManaged { get; }
    public TargetEnvironment Environment { get; }

    /// <summary>Every non-<c>Public</c> Windows user directory the configuration belongs under.
    /// Usually one; more when the prefix kind cannot say, where writing each removes the guess.</summary>
    public IReadOnlyList<string> WindowsUserDirectories { get; }

    public string PrimaryUserDirectory => WindowsUserDirectories[0];

    public string UserRegFile => Path.Combine(Root, "user.reg");

    /// <summary>Evidence of a Proton or umu prefix, which uses <c>steamuser</c> rather than the
    /// invoking user's name.</summary>
    private static readonly string[] ProtonMarkers = ["version", "config_info", "tracked_files"];

    /// <param name="userName">
    /// Injected, never read from <see cref="System.Environment"/>, so discovery is testable —
    /// the same rule <see cref="UnlockerPaths"/> and <c>AppPaths</c> follow.
    /// </param>
    /// <param name="log">
    /// Everything this method has to say; it takes no notes channel at all. Opening a prefix is
    /// speculative — the scanner probes every path five launchers mention — so a rejection is a
    /// fact about a directory nobody claimed was a prefix. What reaches the user is decided one
    /// level up, by the caller that knows whether the user named this path.
    /// </param>
    public static WinePrefix? TryOpen(string candidate, TargetEnvironment environment,
                                      string userName, ILogSink? log = null)
    {
        var sink = log ?? NullLogSink.Instance;
        var (root, missing) = Locate(candidate);
        if (root is null)
        {
            if (missing is not null)
                sink.Write(LogLine.Info($"'{candidate}' is not a Wine prefix: {missing}."));

            return null;
        }

        var driveC = Path.Combine(root, "drive_c");
        var proton = IsProtonManaged(root);

        return new WinePrefix(root, driveC, ReadArch(root, sink), proton, environment,
                              UserDirectories(root, driveC, proton, userName, sink));
    }

    /// <summary>
    /// The directory holding the prefix — <paramref name="candidate"/> or the <c>pfx</c> beneath
    /// it — or the evidence that was missing. Exactly one is non-null, except for an unreadable
    /// path, which is neither. One helper, not two, so <see cref="WhyNotAPrefix"/> cannot drift
    /// from the verdict <see cref="TryOpen"/> acts on.
    /// </summary>
    private static (string? Root, string? Missing) Locate(string candidate)
    {
        var root = PathIdentity.Canonical(candidate);
        if (root is null) return (null, null);

        var missing = MissingPart(root);
        if (missing is null) return (root, null);

        // One level, never recursive. GE-Proton and umu make `pfx` a self-symlink so this is not
        // reached; Valve Proton uses a real subdirectory, and without the retry every
        // Valve-shaped Lutris, Heroic or Bottles prefix is rejected. Recursing would surface
        // prefixes nobody configured.
        var nested = PathIdentity.Canonical(Path.Combine(root, "pfx"));
        if (nested is not null && MissingPart(nested) is null) return (nested, null);

        return (null, missing);
    }

    /// <summary>Why <paramref name="candidate"/> is not a prefix, or null. For the one caller that
    /// must EXPLAIN a rejection: the prefix the user typed themselves. Reopening to recover the
    /// reason would re-report its architecture and users.</summary>
    public static string? WhyNotAPrefix(string candidate) => Locate(candidate).Missing;

    private static string? MissingPart(string root) =>
        !File.Exists(Path.Combine(root, "system.reg")) ? "no system.reg"
        : !Directory.Exists(Path.Combine(root, "drive_c")) ? "no drive_c"
        : null;

    /// <summary>The markers sit at the root for GE-Proton and umu, one level UP for Valve Proton,
    /// whose prefix is <c>compatdata/&lt;id&gt;/pfx</c>. Checking only the root misses every Valve
    /// prefix and then picks the wrong user for the population that always uses
    /// <c>steamuser</c>.</summary>
    private static bool IsProtonManaged(string root)
    {
        var parent = Path.GetDirectoryName(root);

        foreach (var marker in ProtonMarkers)
        {
            if (File.Exists(Path.Combine(root, marker))) return true;
            if (parent is not null && File.Exists(Path.Combine(parent, marker))) return true;
        }

        return false;
    }

    /// <summary>Capped at the header: <c>#arch=</c> is on line four, and <c>system.reg</c> runs to
    /// 38 000 lines, so an uncapped scan reads the whole registry to learn nothing.</summary>
    private const int HeaderLines = 10;

    private static WineArch ReadArch(string root, ILogSink log)
    {
        foreach (var file in new[] { "system.reg", "user.reg" })
        {
            try
            {
                var read = 0;
                foreach (var line in File.ReadLines(Path.Combine(root, file)))
                {
                    if (++read > HeaderLines) break;
                    if (!line.StartsWith("#arch=", StringComparison.Ordinal)) continue;

                    var value = line["#arch=".Length..].Trim();
                    if (string.Equals(value, "win32", StringComparison.OrdinalIgnoreCase))
                        return WineArch.Win32;
                    if (string.Equals(value, "win64", StringComparison.OrdinalIgnoreCase))
                        return WineArch.Win64;
                    break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Fall through to the other file, then to the default. A prefix whose header
                // cannot be read still holds a client that can be unlocked.
            }
        }

        log.Write(LogLine.Info($"'{root}' does not declare a Wine architecture; it is treated as 64-bit."));
        return WineArch.Win64;
    }

    private static IReadOnlyList<string> UserDirectories(string root, string driveC, bool proton,
                                                        string userName, ILogSink log)
    {
        var users = Path.Combine(driveC, "users");
        var expected = proton ? "steamuser" : userName;

        string[] candidates;
        try
        {
            candidates = Directory.Exists(users)
                ? Directory.GetDirectories(users)
                    .Where(d => !string.Equals(Path.GetFileName(d), "Public",
                                               StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            candidates = [];
        }

        // Nothing there yet. The join is returned so the install can create it, which is the same
        // rule ResolveWindowsPath applies to a missing final segment.
        if (candidates.Length == 0) return [Path.Combine(users, expected)];

        var match = candidates.FirstOrDefault(
            d => string.Equals(Path.GetFileName(d), expected, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return [match];

        if (candidates.Length == 1) return candidates;

        // Genuinely ambiguous: the kind-derived name is not among the users that exist, and more
        // than one does. Writing each is idempotent and removes the guess; guessing wrong puts the
        // configuration where the DLL will not look, which is a silent no-op behind a green UI.
        log.Write(LogLine.Warning(
            $"'{root}' has more than one Windows user; the configuration will be written under each."));
        return candidates;
    }

    /// <summary>
    /// A Windows path as this prefix sees it, translated to a Linux path, or null when it cannot
    /// be reached. A missing FINAL segment comes back joined so a caller can create it; a missing
    /// intermediate segment is null.
    /// </summary>
    public string? ResolveWindowsPath(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath) || windowsPath.Length < 2
            || windowsPath[1] != ':')
        {
            return null;
        }

        // Every dosdevices entry is lowercase and the filesystem is case-sensitive, so `C:` fails
        // without this — on every prefix, for every path.
        var letter = char.ToLowerInvariant(windowsPath[0]);
        if (letter is < 'a' or > 'z') return null;

        var target = DriveTarget(letter);

        return target is null ? null : ResolveUnder(target, windowsPath[2..]);
    }

    /// <summary>The segment walk alone, so a caller already holding a Linux directory can continue
    /// from it without re-deriving a drive.</summary>
    public string? ResolveUnder(string linuxBase, string windowsRelative)
    {
        var segments = windowsRelative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = linuxBase;

        for (var i = 0; i < segments.Length; i++)
        {
            var exact = Path.Combine(current, segments[i]);
            if (Directory.Exists(exact) || File.Exists(exact))
            {
                current = exact;
                continue;
            }

            var match = SingleMatch(current, segments[i]);
            if (match is not null)
            {
                current = match;
                continue;
            }

            return i == segments.Length - 1 ? exact : null;
        }

        return current;
    }

    /// <summary>The two roots the unlocker writes under, resolved rather than joined.</summary>
    public UnlockerPaths? PathsFor(string windowsUserDirectory)
    {
        var roaming = ResolveUnder(windowsUserDirectory, @"AppData\Roaming");
        var programData = ResolveWindowsPath(@"C:\ProgramData");

        return roaming is null || programData is null ? null : new UnlockerPaths(roaming, programData);
    }

    /// <summary>Only exactly <c>&lt;letter&gt;:</c> is a drive. The <c>::</c> and
    /// <c>comN</c>/<c>lptN</c> entries are devices, never probed because only the two-character
    /// key is looked up.</summary>
    private string? DriveTarget(char letter)
    {
        var entry = Path.Combine(Root, "dosdevices", $"{letter}:");

        try
        {
            // DirectoryInfo.LinkTarget reports the link's own target without following it and
            // without throwing on a dangling link, which Directory.ResolveLinkTarget does not
            // promise for every shape a real dosdevices carries.
            if (new DirectoryInfo(entry).LinkTarget is { } link)
            {
                return PathIdentity.Canonical(Path.IsPathRooted(link)
                    ? link
                    : Path.Combine(Path.Combine(Root, "dosdevices"), link));
            }

            if (Directory.Exists(entry)) return PathIdentity.Canonical(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            // Falls through to the c: rule below, then to null.
        }

        // Correct for a damaged prefix, and it is what lets these tests run where creating a
        // symlink needs privilege. No other letter falls back: a missing d: resolving inside
        // drive_c would install the unlocker somewhere nobody asked for.
        return letter == 'c' ? DriveC : null;
    }

    /// <summary>One case-insensitive match, or nothing: two entries differing only in case can
    /// both exist here, and picking either guesses which one the client reads.</summary>
    private static string? SingleMatch(string directory, string segment)
    {
        try
        {
            string? found = null;

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!string.Equals(PathIdentity.Normalize(Path.GetFileName(entry)),
                                   PathIdentity.Normalize(segment),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found is not null) return null;
                found = entry;
            }

            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
