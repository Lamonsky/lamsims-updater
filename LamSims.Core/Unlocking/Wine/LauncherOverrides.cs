using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LamSims.Core.Unlocking.Wine;

public enum OverrideVerdict { Absent, SuppliesNative, ForcesBuiltin }

/// <param name="Warnings">Text for each hostile config, naming the file and the game. The
/// environment outranks the registry, so that launch path will not load the unlocker whatever we
/// write — the only useful response is to say where it is.</param>
/// <param name="Unread">Sources that exist but cannot be read, so a clean verdict is not oversold.</param>
public sealed record OverrideFinding(OverrideVerdict Verdict, IReadOnlyList<string> Warnings,
                                     IReadOnlyList<string> Unread);

/// <summary>
/// What each launcher would put in <c>WINEDLLOVERRIDES</c> for a prefix. Read-only throughout.
/// Aggregated across EVERY config naming the prefix, because one prefix routinely has several that
/// disagree — the measured EA app prefix has n,b in two Lutris configs and b,n in a third.
/// </summary>
public sealed class LauncherOverrides(LauncherHomes homes)
{
    public OverrideFinding For(string prefixRoot)
    {
        var verdicts = new List<OverrideVerdict>();
        var warnings = new List<string>();
        var unread = new List<string>();

        foreach (var (file, game, spec) in Sources(prefixRoot, unread))
        {
            var verdict = Classify(spec);
            if (verdict == OverrideVerdict.Absent) { verdicts.Add(verdict); continue; }

            verdicts.Add(verdict);

            if (verdict == OverrideVerdict.ForcesBuiltin)
            {
                warnings.Add($"'{file}' forces Wine's own version.dll for '{game}', which "
                             + "outranks the registry. The unlocker will not load when the game is "
                             + "launched that way.");
            }
        }

        // Hostile wins outright: that launch path is broken for the unlocker and the user needs to
        // know. "Every config supplies it" needs EVERY one — a config that says nothing is a launch
        // path with no override, so the registry write is still needed.
        var overall = verdicts.Contains(OverrideVerdict.ForcesBuiltin) ? OverrideVerdict.ForcesBuiltin
            : verdicts.Count > 0 && verdicts.All(v => v == OverrideVerdict.SuppliesNative)
                ? OverrideVerdict.SuppliesNative
                : OverrideVerdict.Absent;

        return new OverrideFinding(overall, warnings, unread);
    }

    /// <summary>Every config naming this prefix, as (file, game, spec) — whatever that launcher
    /// would put in WINEDLLOVERRIDES, in mapping or string form.</summary>
    private IEnumerable<(string File, string Game, string? Spec)> Sources(
        string prefixRoot, List<string> unread)
    {
        // Resolved, not canonicalised: prefixRoot came from WinePrefixScanner.Resolve and may have
        // descended into "pfx". Anything weaker leaves the config side unmatched, so the prefix
        // reads Absent while a hostile override goes unreported.
        var wanted = WinePrefixScanner.Resolve(prefixRoot) ?? prefixRoot;

        foreach (var home in homes.For(EnvironmentSource.Lutris))
        {
            foreach (var file in Files(Path.Combine(home.Root, "games"), "*.yml", "Lutris", unread))
            {
                if (!Names(file, "prefix", wanted)) continue;

                var game = WinePrefixScanner.ReadLineValue(file, "name")
                           ?? Path.GetFileNameWithoutExtension(file);

                // Two shapes, and a real machine has both. The mapping form is `version.dll: n,b`
                // under wine.overrides; the string form is a WINEDLLOVERRIDES under system.env.
                var versionDll = WinePrefixScanner.ReadLineValue(file, "version.dll");
                if (versionDll is not null)
                    yield return (file, game, $"version.dll: {versionDll}");

                var wineDllOverrides = WinePrefixScanner.ReadLineValue(file, "WINEDLLOVERRIDES");
                if (wineDllOverrides is not null)
                    yield return (file, game, wineDllOverrides);

                // A config file that names this prefix but has no override sources is absent.
                if (versionDll is null && wineDllOverrides is null)
                    yield return (file, game, null);
            }
        }

        foreach (var home in homes.For(EnvironmentSource.Heroic))
        {
            foreach (var file in Files(Path.Combine(home.Root, "GamesConfig"), "*.json", "Heroic",
                                       unread))
            {
                foreach (var found in HeroicSources(file, wanted, unread)) yield return found;
            }
        }

        foreach (var home in homes.For(EnvironmentSource.Bottles))
        {
            // A bottle IS the prefix, so the only bottle naming it is itself, and only under that
            // bottles root — without the parent check a stray bottle.yml in any prefix would read
            // as its Bottles config. Resolved on both sides, as for `wanted`.
            var parentDir = Path.GetDirectoryName(wanted);
            var parent = parentDir is null ? null : WinePrefixScanner.Resolve(parentDir);
            if (parent is null || parent != (WinePrefixScanner.Resolve(home.Root) ?? home.Root))
                continue;

            var yml = Path.Combine(prefixRoot, "bottle.yml");
            if (!File.Exists(yml)) continue;

            var name = WinePrefixScanner.ReadLineValue(yml, "Name") ?? Path.GetFileName(prefixRoot);

            // Bottles writes a DLL_Overrides MAPPING. Read only from that section to avoid
            // colliding with other schema keys like `version:` (not a DLL override).
            var version = ReadBottlesDllOverride(yml, "version");
            if (version is not null)
                yield return (yml, name, $"version: {version}");
        }

        foreach (var found in SteamSources(prefixRoot, unread)) yield return found;
    }

    private IEnumerable<(string File, string Game, string? Spec)> HeroicSources(
        string file, string wanted, List<string> unread)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            unread.Add($"Heroic: '{file}' could not be read: {e.Message}.");
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                unread.Add($"Heroic: '{file}' root is not an object.");
                yield break;
            }

            foreach (var game in document.RootElement.EnumerateObject())
            {
                if (game.Value.ValueKind != JsonValueKind.Object) continue;
                if (!game.Value.TryGetProperty("winePrefix", out var prefix)
                    || prefix.ValueKind != JsonValueKind.String
                    || !MatchesPrefix(prefix.GetString(), wanted))
                {
                    continue;
                }

                var title = game.Value.TryGetProperty("title", out var t)
                            && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? game.Name
                    : game.Name;

                // Both spellings. "enviromentOptions" is the one Heroic actually ships and it is in
                // every file on the measured machine; the correctly spelled one is in none, so
                // reading only that finds nothing on every Heroic install.
                foreach (var property in new[] { "enviromentOptions", "environmentOptions" })
                {
                    if (!game.Value.TryGetProperty(property, out var options)
                        || options.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    // A list of {key, value} objects, not a map.
                    foreach (var entry in options.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        if (!entry.TryGetProperty("key", out var key)
                            || key.ValueKind != JsonValueKind.String
                            || !string.Equals(key.GetString(), "WINEDLLOVERRIDES",
                                              StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        yield return (file, title,
                                      entry.TryGetProperty("value", out var v)
                                      && v.ValueKind == JsonValueKind.String ? v.GetString() : null);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The app id IS the compatdata directory name, so the prefix path alone identifies the app and
    /// nothing extra has to be threaded through.
    /// </summary>
    private IEnumerable<(string File, string Game, string? Spec)> SteamSources(
        string prefixRoot, List<string> unread)
    {
        var container = Path.GetDirectoryName(PathIdentity.Canonical(prefixRoot) ?? prefixRoot);
        var appId = container is null ? null : Path.GetFileName(container);
        if (appId is null || !appId.All(char.IsDigit)) yield break;

        foreach (var home in homes.For(EnvironmentSource.Steam))
        {
            foreach (var user in WinePrefixScanner.Children(Path.Combine(home.Root, "userdata")))
            {
                // Out of scope and REPORTED: non-Steam shortcuts keep their launch options in a
                // binary file, and a user whose hostile override lives there would otherwise be
                // told everything is fine.
                var shortcuts = Path.Combine(user, "config", "shortcuts.vdf");
                if (File.Exists(shortcuts))
                {
                    unread.Add($"Steam: '{shortcuts}' is a binary file and was not read, so a "
                               + "non-Steam shortcut's launch options are unknown.");
                }

                var file = Path.Combine(user, "config", "localconfig.vdf");
                if (!File.Exists(file)) continue;

                var options = LaunchOptions(file, appId, unread);
                if (options is not null) yield return (file, appId, options);
            }
        }
    }

    /// <summary>The LaunchOptions value in the app's own block, unescaped. Scanned rather than
    /// parsed as a tree: a VDF parser is a lot of surface for one string.</summary>
    private static string? LaunchOptions(string file, string appId, List<string> unread)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            unread.Add($"Steam: '{file}' could not be read: {e.Message}.");
            return null;
        }

        var inApp = false;
        var braceDepth = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line == $"\"{appId}\"") { inApp = true; braceDepth = 0; continue; }
            if (!inApp) continue;

            // Depth tracking, because a nested object's closing brace would otherwise stop the
            // scan early and leave LaunchOptions unread.
            foreach (var ch in line)
            {
                if (ch == '{') braceDepth++;
                else if (ch == '}') braceDepth--;
            }

            if (braceDepth <= 0) { inApp = false; continue; }

            if (!line.StartsWith("\"LaunchOptions\"", StringComparison.OrdinalIgnoreCase)) continue;

            var open = line.IndexOf('"', "\"LaunchOptions\"".Length);
            if (open < 0) continue;

            var builder = new System.Text.StringBuilder();
            for (var i = open + 1; i < line.Length; i++)
            {
                if (line[i] == '\\' && i + 1 < line.Length) { builder.Append(line[++i]); continue; }
                if (line[i] == '"') break;
                builder.Append(line[i]);
            }

            var launchOptions = builder.ToString();

            // Format: WINEDLLOVERRIDES=<value> [more options]
            foreach (var part in launchOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.StartsWith("WINEDLLOVERRIDES=", StringComparison.OrdinalIgnoreCase)) continue;

                var value = part["WINEDLLOVERRIDES=".Length..];

                // Strip the surrounding quotes. Never a `\"`-prefixed branch here: the per-character
                // unescaping loop above has already turned every `\"` into a plain `"`, so only the
                // plain-quote case can ever be seen at this point.
                if (value.StartsWith('"'))
                    value = value[1..];

                if (value.EndsWith('"'))
                    value = value[..^1];

                return string.IsNullOrEmpty(value) ? null : value;
            }

            return null;
        }

        return null;
    }

    /// <summary>A DLL override from a bottle.yml, read only inside DLL_Overrides — the top level
    /// has its own `version:` key, which is a schema version.</summary>
    private static string? ReadBottlesDllOverride(string file, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var inDllOverrides = false;
        var headerIndent = -1;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (!inDllOverrides)
            {
                if (trimmed.StartsWith("DLL_Overrides:", StringComparison.OrdinalIgnoreCase))
                {
                    inDllOverrides = true;
                    headerIndent = indent;
                }

                continue;
            }

            if (indent <= headerIndent && !trimmed.StartsWith("DLL_Overrides:", StringComparison.OrdinalIgnoreCase))
                break;

            if (indent > headerIndent && trimmed.StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase))
            {
                var valueStart = key.Length + 1;
                if (valueStart < trimmed.Length)
                {
                    var value = trimmed[valueStart..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }

        return null;
    }

    private static bool Names(string file, string key, string wanted) =>
        WinePrefixScanner.ReadLineValues(file, key).Any(value => MatchesPrefix(value, wanted));

    /// <summary>Whether a launcher-declared path names this prefix. Through
    /// <see cref="WinePrefixScanner.Resolve"/> on both sides, never Canonical alone, and also
    /// trying <c>&lt;value&gt;/pfx</c> — the same descent <see cref="WinePrefix.TryOpen"/>
    /// performs.</summary>
    private static bool MatchesPrefix(string? value, string wanted)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (WinePrefixScanner.Resolve(value) is { } direct
            && string.Equals(direct, wanted, StringComparison.Ordinal))
        {
            return true;
        }

        var nested = WinePrefixScanner.Resolve(Path.Combine(value, "pfx"));
        return nested is not null && string.Equals(nested, wanted, StringComparison.Ordinal);
    }

    /// <param name="source">Named in a reported failure, matching the "Heroic: ..." and "Steam: ..."
    /// prefix every other read failure in this class already carries.</param>
    private static IEnumerable<string> Files(string directory, string pattern, string source,
                                             List<string> unread)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            unread.Add($"{source}: '{directory}' could not be read: {e.Message}.");
            return [];
        }
    }

    /// <summary>
    /// Three-valued: two values cannot separate "no override" from "an override forcing Wine's own
    /// DLL", so <c>b,n</c> would read as absent and the run would report success while the unlocker
    /// never loads. Matches <c>version</c> with or without <c>.dll</c> and <c>*</c>,
    /// case-insensitively, and never as a substring — <c>versioncheck</c> must not match.
    /// </summary>
    public static OverrideVerdict Classify(string? overrides)
    {
        if (string.IsNullOrWhiteSpace(overrides)) return OverrideVerdict.Absent;

        foreach (var entry in overrides.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = entry.IndexOf('=');
            if (at < 0)
            {
                // The mapping form arrives as `version.dll: n,b`, whose colon this handles.
                at = entry.IndexOf(':');
                if (at < 0) continue;
            }

            // One entry can name several modules sharing a value (`comdlg32,version=n,b`), and
            // reading the left side whole matches none of them.
            if (!NamesVersion(entry[..at])) continue;

            var value = entry[(at + 1)..].Trim().Trim('"');

            // Only the first field decides what loads. An absent first field is Wine's "disabled" —
            // the DLL never loads, so reading it as "absent" would report success. A value of
            // separators alone means the same.
            // String.Split always yields at least one element, so the first field is indexed
            // directly; an empty value gives one empty field, which is the disabled case below.
            var first = value.Split(',')[0].Trim();
            if (first.Length == 0) return OverrideVerdict.ForcesBuiltin;

            return first.StartsWith("n", StringComparison.OrdinalIgnoreCase)
                ? OverrideVerdict.SuppliesNative
                : OverrideVerdict.ForcesBuiltin;
        }

        return OverrideVerdict.Absent;
    }

    /// <summary>Whether a module list names <c>version</c>. The <c>*</c> of a path-independent
    /// entry and a spelled-out <c>.dll</c> are both stripped.</summary>
    private static bool NamesVersion(string modules)
    {
        foreach (var candidate in modules.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var module = candidate.Trim().Trim('"').TrimStart('*');
            if (module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                module = module[..^4];

            if (string.Equals(module, "version", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>A per-application entry under <c>AppDefaults\&lt;exe&gt;\DllOverrides</c> silently
    /// outranks the global block, and winecfg's per-application tab is how one is acquired. Read
    /// and reported, never written.</summary>
    public static string? AppDefaultsConflict(WinePrefix prefix, string clientExe)
    {
        var key = $@"Software\Wine\AppDefaults\{clientExe}\DllOverrides";
        var values = WineRegistryFile.ReadKey(prefix.UserRegFile, key);

        if (values is null) return null;
        if (!values.ContainsKey("version") && !values.ContainsKey("*version")) return null;

        return $"This prefix has a per-application 'version' override for '{clientExe}', which "
               + "takes precedence over the one written here.";
    }
}
