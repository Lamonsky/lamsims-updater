using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LamSims.Core.Logging;

namespace LamSims.Core.Unlocking.Wine;

/// <param name="Detail">Identifies this candidate within its source, before dedup. Several
/// candidates can name one prefix, so this is a proposal, not the final label.</param>
public sealed record PrefixCandidate(string Path, EnvironmentSource Source, string Detail,
                                     bool Flatpak);

/// <summary>
/// Finds Wine prefixes by asking each launcher's own configuration, then validating what it finds.
/// Extract loosely, validate strictly: <see cref="WinePrefix.TryOpen"/> rejects a wrong extraction
/// with a reason, which is what makes line-oriented parsing safe without a YAML or VDF dependency.
///
/// Every source is best-effort — a malformed config yields a diagnostic and no candidates, never
/// an exception. One broken game file must not stop the scan.
/// </summary>
public sealed class WinePrefixScanner(LauncherHomes homes, string userName,
                                      IProgress<string>? notes = null,
                                      string flatpakInfoFile = "/.flatpak-info",
                                      ILogSink? log = null)
{
    /// <summary>Where per-candidate rejections go. Five launchers mention far more paths than are
    /// prefixes, and routing each rejection to <c>notes</c> put thirty lines of "nobody claimed
    /// this was a prefix" above the Install button. <c>notes</c> is what the user must act on.</summary>
    private readonly ILogSink _log = log ?? NullLogSink.Instance;

    public IReadOnlyList<WinePrefix> Scan(string? configuredPrefix)
    {
        var candidates = new List<PrefixCandidate>();

        // The user's own setting first, so a prefix they named explicitly is never crowded out.
        if (!string.IsNullOrWhiteSpace(configuredPrefix))
            candidates.Add(new PrefixCandidate(configuredPrefix, EnvironmentSource.Wine,
                                               configuredPrefix, false));

        candidates.AddRange(FromWine());

        candidates.AddRange(FromSteam());
        candidates.AddRange(FromLutris());
        candidates.AddRange(FromHeroic());
        candidates.AddRange(FromBottles());

        var found = Describe(candidates);

        // The one candidate the user typed, so its rejection answers something they did. Asked
        // directly rather than recovered from Describe: reopening would re-report the prefix's
        // architecture and users, and a membership test over `found` cannot tell a rejection from
        // a dedup into another group.
        if (!string.IsNullOrWhiteSpace(configuredPrefix)
            && WinePrefix.WhyNotAPrefix(configuredPrefix) is { } why)
        {
            notes?.Report($"The Wine prefix setting names '{configuredPrefix}', which is not a "
                          + $"Wine prefix: {why}.");
        }

        if (found.Count == 0)
        {
            if (File.Exists(flatpakInfoFile))
            {
                notes?.Report("This build runs in a Flatpak sandbox and cannot see host paths "
                              + "without --filesystem=home.");
            }
            else
            {
                // The only route left once every launcher's own configuration has come up empty:
                // an unlisted launcher, or a prefix kept somewhere none of the five sources look.
                notes?.Report("No Wine prefix was found. If your client is installed somewhere "
                              + "Steam, Lutris, Heroic, Bottles or plain Wine would not know about, "
                              + "set its path in the Wine prefix setting.");
            }
        }

        return found;
    }

    /// <summary>Dedup, label and open. Internal so a test can drive the labelling rules directly:
    /// reaching them through six extraction sources would make a labelling failure read as a
    /// discovery failure.</summary>
    internal IReadOnlyList<WinePrefix> Describe(IReadOnlyList<PrefixCandidate> candidates)
    {
        var opened = new List<WinePrefix>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // RESOLVED, not canonical: ~/.steam/steam is a symlink, and Canonical is GetFullPath,
        // which never follows one — so canonical-only dedup shows every Steam prefix twice.
        //
        // Ordinal, because on a Linux filesystem "~/Games/Prefix" and "~/Games/prefix" are two
        // prefixes: folding them dropped the second before it could become a target.
        foreach (var group in candidates.GroupBy(c => Resolve(c.Path) ?? c.Path,
                                                 StringComparer.Ordinal))
        {
            var first = group.First();
            var count = group.Count();

            // Several configs routinely name one prefix, and they disagree about which game it is,
            // so a name taken from one of them is not reproducible between runs. The launcher and
            // the count are.
            var detail = count > 1
                ? $"{count} games"
                : first.Detail is { Length: > 0 } named ? named : DirectoryName(group.Key);

            var prefix = WinePrefix.TryOpen(group.Key, new TargetEnvironment(
                first.Source, detail, first.Flatpak), userName, _log);
            if (prefix is null) continue;

            // The resolved path is the group key, but TryOpen may have descended into `pfx`, so the
            // final identity check uses what it actually opened.
            if (!seen.Add(PathIdentity.Canonical(prefix.Root) ?? prefix.Root)) continue;

            opened.Add(prefix);
        }

        return opened;
    }

    private static string DirectoryName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

        return name.Length > 0 ? name : path;
    }

    /// <summary>
    /// The physical path with EVERY symlinked component resolved, not just the last.
    /// <c>Directory.ResolveLinkTarget</c> follows only the final component, and the link that
    /// matters is routinely in the middle: <c>~/.steam/steam</c> is a symlink, and Steam candidates
    /// hang below it at <c>steamapps/compatdata/*/pfx</c>, which is not itself a link.
    ///
    /// Internal because this is the identity rule everywhere a symlinked home or prefix component
    /// appears — <see cref="LauncherHomes.For"/> and <see cref="LauncherOverrides"/> both compare
    /// by it, since <see cref="PathIdentity.Canonical"/> is lexical and disagrees with what was
    /// actually opened.
    /// </summary>
    internal static string? Resolve(string path)
    {
        var canonical = PathIdentity.Canonical(path);
        if (canonical is null) return null;

        try
        {
            // Walked from the path root so the drive or leading separator survives: on Windows the
            // root is "C:\\" and splitting it away would produce a path relative to the working
            // directory.
            var root = Path.GetPathRoot(canonical) ?? "";
            var current = canonical;
            var passes = 0;
            const int maxPasses = 40; // Linux caps ELOOP at roughly this; the cap is what makes a
                                       // self-referential link terminate instead of hanging.

            while (passes < maxPasses)
            {
                passes++;

                        var cursor = root;
                var parts = current[root.Length..]
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

                var foundLink = false;
                for (var i = 0; i < parts.Length; i++)
                {
                    cursor = Path.Combine(cursor, parts[i]);

                    // DirectoryInfo and FileInfo both read the link data, so one of the two
                    // answers for either kind, and neither throws on a component that does not
                    // exist or whose target is missing.
                    var target = new DirectoryInfo(cursor).LinkTarget
                                 ?? new FileInfo(cursor).LinkTarget;
                    if (target is null) continue;

                            var resolved = PathIdentity.Canonical(Path.IsPathRooted(target)
                                      ? target
                                      : Path.Combine(Path.GetDirectoryName(cursor) ?? root, target))
                                  ?? cursor;

                            var remaining = string.Join(Path.DirectorySeparatorChar.ToString(),
                                               parts[(i + 1)..]);
                    current = remaining.Length > 0
                        ? Path.Combine(resolved, remaining)
                        : resolved;

                    foundLink = true;
                    break; // Restart the walk from the beginning with the new path.
                }

                if (!foundLink) break; // No link found in this pass; path is final.
            }

            return PathIdentity.Canonical(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            // An unreadable component leaves the path unresolved rather than dropping the
            // candidate: validation is what rejects a bad path, not this.
            return canonical;
        }
    }

    private IEnumerable<PrefixCandidate> FromWine()
    {
        if (homes.WinePrefixEnvironment is { } fromEnvironment)
            yield return new PrefixCandidate(fromEnvironment, EnvironmentSource.Wine,
                                             "$WINEPREFIX", false);

        foreach (var home in homes.For(EnvironmentSource.Wine))
        {
            // ~/.wine IS a prefix; <data>/wineprefixes and the Flatpak wine directory are
            // containers of them. Both shapes are offered and validation sorts them out.
            yield return new PrefixCandidate(home.Root, EnvironmentSource.Wine,
                                             DirectoryName(home.Root), home.Flatpak);

            foreach (var child in Children(home.Root))
                yield return new PrefixCandidate(child, EnvironmentSource.Wine,
                                                 DirectoryName(child), home.Flatpak);
        }
    }

    /// <summary>One level. Never a recursive walk: a prefix's own drive_c would swallow the scan.</summary>
    internal static IEnumerable<string> Children(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The first <c>key: value</c> in a line-oriented config, unquoted and stripped of
    /// inline comments. Deliberately not a YAML parser: validation rejects a wrong extraction.</summary>
    internal static string? ReadLineValue(string file, string key)
    {
        foreach (var value in ReadLineValues(file, key)) return value;

        return null;
    }

    internal static IEnumerable<string> ReadLineValues(string file, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase)) continue;

            var value = Clean(line[(key.Length + 1)..]);
            if (value is not null) yield return value;
        }
    }

    /// <summary>A launcher config that could not be read. The log, not the notes: the scan carries
    /// on, and a user with five launchers would collect these for ones they never configured. A
    /// warning, because an unreadable config can hide a prefix they expected to see.</summary>
    private void Diagnostic(string message) => _log.Write(LogLine.Warning(message));

    private static string? Clean(string value)
    {
        value = value.Trim();

        if (value.StartsWith('"') || value.StartsWith('\''))
        {
            var quote = value[0];
            var end = value.IndexOf(quote, 1);
            return end > 1 ? value[1..end] : null;
        }

        var comment = value.IndexOf('#');
        if (comment >= 0) value = value[..comment].TrimEnd();

        return value.Length == 0 ? null : value;
    }

    private IEnumerable<PrefixCandidate> FromSteam()
    {
        foreach (var home in homes.For(EnvironmentSource.Steam))
        {
            foreach (var library in Libraries(home.Root))
            {
                var compatdata = Path.Combine(library, "steamapps", "compatdata");

                foreach (var container in Children(compatdata))
                {
                    var appId = Path.GetFileName(container);

                    // Not an app. Without this every machine with Steam reports a phantom target or
                    // a rejection note about one.
                    if (appId == "0") continue;

                    yield return new PrefixCandidate(Path.Combine(container, "pfx"),
                                                     EnvironmentSource.Steam,
                                                     Label(container, appId), home.Flatpak);
                }
            }
        }
    }

    /// <summary>The root plus every path in its own library list, keyed off the home because a
    /// Flatpak Steam's list is its own.</summary>
    private IEnumerable<string> Libraries(string root)
    {
        // Ordinal for the same reason as Describe: two library paths differing only in case are
        // two directories here.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = PathIdentity.Canonical(root) ?? root;
        if (seen.Add(resolved)) yield return root;

        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) yield break;

        foreach (var path in VdfValues(file, "path"))
        {
            var pathResolved = PathIdentity.Canonical(path) ?? path;
            if (seen.Add(pathResolved)) yield return path;
        }
    }

    /// <summary>Every <c>"key" "value"</c> pair with this key. VDF values carry <c>\\</c> and
    /// <c>\"</c> escapes that a raw read would leave in the path.</summary>
    private IEnumerable<string> VdfValues(string file, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostic($"Steam: '{file}' could not be read: {e.Message}.");
            return [];
        }

        return lines.Select(line => VdfPair(line, key)).Where(v => v is not null).Select(v => v!);
    }

    private static string? VdfPair(string line, string key)
    {
        var trimmed = line.Trim();
        var quoted = $"\"{key}\"";
        if (!trimmed.StartsWith(quoted, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = trimmed[quoted.Length..];
        var open = rest.IndexOf('"');
        if (open < 0) return null;

        var builder = new System.Text.StringBuilder();
        for (var i = open + 1; i < rest.Length; i++)
        {
            if (rest[i] == '\\' && i + 1 < rest.Length) { builder.Append(rest[++i]); continue; }
            if (rest[i] == '"') return builder.Length == 0 ? null : builder.ToString();
            builder.Append(rest[i]);
        }

        return null;
    }

    /// <summary>The build is NOT config_info line 1: GE-Proton writes its name there, Valve writes
    /// a version number with the build only in the later path lines. Taking line 1 labels every
    /// Valve prefix with a version that names no build.</summary>
    private static string Label(string container, string appId)
    {
        var build = BuildFromConfigInfo(Path.Combine(container, "config_info"))
                    ?? FirstLine(Path.Combine(container, "config_info"))
                    ?? FirstLine(Path.Combine(container, "version"));

        return build is null ? $"app {appId}" : $"{build}, app {appId}";
    }

    private static string? BuildFromConfigInfo(string file)
    {
        const string marker = "steamapps/common/";

        foreach (var line in Lines(file))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;

            var rest = line[(at + marker.Length)..];
            var end = rest.IndexOf('/');
            if (end > 0) return rest[..end];
        }

        return null;
    }

    private static string? FirstLine(string file)
    {
        foreach (var line in Lines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return null;
    }

    private static IEnumerable<string> Lines(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IEnumerable<PrefixCandidate> FromLutris()
    {
        foreach (var home in homes.For(EnvironmentSource.Lutris))
        {
            foreach (var file in Files(Path.Combine(home.Root, "games"), "*.yml"))
            {
                // The in-file name, never the filename: a real Lutris config is called
                // ea-app-1778070803.yml and that suffix means nothing to the user.
                var label = ReadLineValue(file, "name") ?? ReadLineValue(file, "game_slug")
                            ?? Path.GetFileNameWithoutExtension(file);

                // Every prefix: line in the file, not just the first: a config can carry more than
                // one and taking the first would silently drop the rest.
                foreach (var prefix in ReadLineValues(file, "prefix"))
                    yield return new PrefixCandidate(prefix, EnvironmentSource.Lutris, label,
                                                     home.Flatpak);
            }
        }
    }

    private IEnumerable<PrefixCandidate> FromHeroic()
    {
        foreach (var home in homes.For(EnvironmentSource.Heroic))
        {
            foreach (var file in Files(Path.Combine(home.Root, "GamesConfig"), "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (ReadJson(file, "Heroic") is not { } document) continue;

                using (document)
                {
                    // Guard the shape first: EnumerateObject throws on a non-object root, and that
                    // would escape to Scan and kill the whole discovery chain.
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        Diagnostic($"Heroic: '{file}' root is not a JSON object, skipping.");
                        continue;
                    }

                    foreach (var game in document.RootElement.EnumerateObject())
                    {
                        if (game.Value.ValueKind != JsonValueKind.Object) continue;
                        if (!TryString(game.Value, "winePrefix", out var prefix)) continue;

                        var label = TryString(game.Value, "title", out var title) ? title : id;
                        yield return new PrefixCandidate(prefix, EnvironmentSource.Heroic, label,
                                                         home.Flatpak);
                    }
                }
            }

            // defaultSettings.winePrefix is a CONTAINER of one prefix per game, not a prefix: it
            // has neither system.reg nor drive_c, so treating it as one rejects every Heroic user
            // and finds none of the prefixes inside it.
            var config = Path.Combine(home.Root, "config.json");
            if (ReadJson(config, "Heroic") is not { } settings) continue;

            using (settings)
            {
                // Guard the shape: TryGetProperty requires an object root, and throws if the root
                // is not an object.
                if (settings.RootElement.ValueKind != JsonValueKind.Object)
                {
                    Diagnostic($"Heroic: '{config}' root is not a JSON object, skipping.");
                    continue;
                }

                if (!settings.RootElement.TryGetProperty("defaultSettings", out var defaults)
                    || defaults.ValueKind != JsonValueKind.Object
                    || !TryString(defaults, "winePrefix", out var container))
                {
                    continue;
                }

                foreach (var child in Children(container))
                    yield return new PrefixCandidate(child, EnvironmentSource.Heroic,
                                                     DirectoryName(child), home.Flatpak);
            }
        }
    }

    private IEnumerable<PrefixCandidate> FromBottles()
    {
        foreach (var home in homes.For(EnvironmentSource.Bottles))
        {
            // A bottle IS a prefix, so no config has to be read to find one; bottle.yml is read
            // only for the label the user gave it.
            foreach (var bottle in Children(home.Root))
            {
                var label = ReadLineValue(Path.Combine(bottle, "bottle.yml"), "Name")
                            ?? DirectoryName(bottle);

                yield return new PrefixCandidate(bottle, EnvironmentSource.Bottles, label,
                                                 home.Flatpak);
            }
        }
    }

    private IEnumerable<string> Files(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostic($"'{directory}' could not be read: {e.Message}.");
            return [];
        }
    }

    /// <summary>A parsed document, or null with a diagnostic. Malformed JSON is ordinary with
    /// several launchers installed and must not stop the other sources.</summary>
    private JsonDocument? ReadJson(string file, string source)
    {
        try
        {
            return File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostic($"{source}: '{file}' could not be read: {e.Message}.");
            return null;
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";

        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";

        return value.Length > 0;
    }
}
