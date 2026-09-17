using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LamSims.Core.Unlocking.Wine;

/// <param name="Flatpak">
/// A launcher's native and Flatpak installs produce prefixes whose source and detail are
/// otherwise identical, so this is what separates their rows in the UI.
/// </param>
public sealed record LauncherHome(EnvironmentSource Source, string Root, bool Flatpak);

/// <summary>
/// Where each launcher keeps its configuration, natively and as a Flatpak. The home directory and
/// both XDG values are INJECTED and this type never calls <see cref="Environment"/>, mirroring
/// <see cref="UnlockerPaths"/> and <c>AppPaths</c>: without that, discovery is untestable.
/// </summary>
public sealed class LauncherHomes
{
    private readonly List<LauncherHome> _homes = [];

    /// <param name="winePrefix">
    /// <c>$WINEPREFIX</c>. Carried separately rather than added to the Wine homes, because it names
    /// a prefix directly while every home is a container the scanner looks inside; mixing them makes
    /// the scanner treat a prefix's own drive_c entries as prefixes.
    /// </param>
    public LauncherHomes(string home, string? xdgConfigHome, string? xdgDataHome, string? winePrefix)
    {
        Home = home;
        ConfigRoot = string.IsNullOrWhiteSpace(xdgConfigHome)
            ? Path.Combine(home, ".config") : xdgConfigHome;
        DataRoot = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(home, ".local", "share") : xdgDataHome;
        WinePrefixEnvironment = string.IsNullOrWhiteSpace(winePrefix) ? null : winePrefix;

        Add(EnvironmentSource.Steam, false,
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(DataRoot, "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
        Add(EnvironmentSource.Steam, true,
            Path.Combine(Var(home, "com.valvesoftware.Steam"), ".local", "share", "Steam"));

        // Both roots, always. On a stock Lutris install ~/.config/lutris does not
        // exist and every game config lives under the data root.
        Add(EnvironmentSource.Lutris, false,
            Path.Combine(DataRoot, "lutris"), Path.Combine(ConfigRoot, "lutris"));
        Add(EnvironmentSource.Lutris, true,
            Path.Combine(Var(home, "net.lutris.Lutris"), "data", "lutris"),
            Path.Combine(Var(home, "net.lutris.Lutris"), "config", "lutris"));

        Add(EnvironmentSource.Heroic, false, Path.Combine(ConfigRoot, "heroic"));
        Add(EnvironmentSource.Heroic, true,
            Path.Combine(Var(home, "com.heroicgameslauncher.hgl"), "config", "heroic"));

        Add(EnvironmentSource.Bottles, false, Path.Combine(DataRoot, "bottles", "bottles"));
        Add(EnvironmentSource.Bottles, true,
            Path.Combine(Var(home, "com.usebottles.bottles"), "data", "bottles", "bottles"));

        Add(EnvironmentSource.Wine, false,
            Path.Combine(home, ".wine"), Path.Combine(DataRoot, "wineprefixes"));
        Add(EnvironmentSource.Wine, true, Path.Combine(Var(home, "org.winehq.Wine"), "data", "wine"));
    }

    public string Home { get; }
    public string ConfigRoot { get; }
    public string DataRoot { get; }
    public string? WinePrefixEnvironment { get; }

    /// <summary>
    /// Filtered by existence HERE, not when the list was built. A launcher can appear after this
    /// object was constructed — a Flatpak installed while the application runs, or a test that
    /// writes a config after building the graph — and an eagerly filtered list would never see it.
    /// The candidate paths themselves are fixed; only whether they exist is re-checked.
    ///
    /// Deduplicated by RESOLVED root, not by the candidate path itself: on a normal Steam install
    /// <c>~/.steam/steam</c> and <c>~/.steam/root</c> are both symlinks to <c>&lt;data&gt;/Steam</c>,
    /// so without this every Steam prefix is discovered — and its compatdata walked, and its
    /// overrides reported — up to three times over, and <c>Describe</c> then reads "more than one
    /// candidate names this prefix" as "several games share it" and mislabels the row. Resolved by
    /// <see cref="WinePrefixScanner.Resolve"/>, which walks every component rather than only the
    /// last, unlike <see cref="PathIdentity.Canonical"/> alone.
    /// </summary>
    public IReadOnlyList<LauncherHome> For(EnvironmentSource source)
    {
        // Ordinal: a launcher home is a whole path on a Linux filesystem, where two spellings
        // are two directories.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<LauncherHome>();

        foreach (var home in _homes.Where(h => h.Source == source && Exists(h.Root)))
        {
            var resolved = WinePrefixScanner.Resolve(home.Root) ?? home.Root;
            if (seen.Add(resolved)) found.Add(home);
        }

        return found;
    }

    private static bool Exists(string root)
    {
        try
        {
            return Directory.Exists(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable candidate is not a home. Discovery is best-effort throughout.
            return false;
        }
    }

    private static string Var(string home, string id) => Path.Combine(home, ".var", "app", id);

    /// <summary>
    /// Records the candidates. Whether each one exists is decided in <see cref="For"/>, per call:
    /// a home that appears later must still be found, and only <see cref="For"/> knows when "later"
    /// is. What this method fixes is the SET of paths, which never changes for a given home and XDG
    /// layout.
    /// </summary>
    private void Add(EnvironmentSource source, bool flatpak, params string[] roots)
    {
        foreach (var root in roots) _homes.Add(new LauncherHome(source, root, flatpak));
    }
}
