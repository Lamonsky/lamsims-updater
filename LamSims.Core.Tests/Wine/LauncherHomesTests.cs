using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class LauncherHomesTests
{
    private static LauncherHomes Homes(TempDir dir, string? wine = null) =>
        new(dir.Path, xdgConfigHome: null, xdgDataHome: null, winePrefix: wine);

    private static string Make(TempDir dir, params string[] parts)
    {
        var path = Path.Combine(new[] { dir.Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static IEnumerable<string> Roots(LauncherHomes homes, EnvironmentSource source) =>
        homes.For(source).Select(h => h.Root);

    /// <summary>
    /// ~/.config and ~/.local/share when the XDG variables are unset, which is the ordinary case.
    /// Read from the injected values, never from Environment: without that, discovery is untestable.
    /// </summary>
    [Fact]
    public void The_xdg_roots_default_under_the_given_home()
    {
        using var dir = new TempDir();
        var homes = Homes(dir);

        Assert.Equal(Path.Combine(dir.Path, ".config"), homes.ConfigRoot);
        Assert.Equal(Path.Combine(dir.Path, ".local", "share"), homes.DataRoot);
    }

    [Fact]
    public void The_xdg_variables_override_the_defaults()
    {
        using var dir = new TempDir();
        var homes = new LauncherHomes(dir.Path, xdgConfigHome: "/etc/x", xdgDataHome: "/srv/y",
                                     winePrefix: null);

        Assert.Equal("/etc/x", homes.ConfigRoot);
        Assert.Equal("/srv/y", homes.DataRoot);
    }

    /// <summary>
    /// Only directories that exist. A home list full of paths nobody has means every extraction
    /// source runs on nothing, and the "flatpak-only" and "native-only" cases below could not be
    /// told apart.
    /// </summary>
    [Fact]
    public void A_launcher_that_is_not_installed_has_no_homes()
    {
        using var dir = new TempDir();

        Assert.Empty(Homes(dir).For(EnvironmentSource.Lutris));
    }

    /// <summary>
    /// Existence is re-checked per call, not frozen at construction. A caller builds this once —
    /// Composition does, and so does every operation fixture — and a launcher config written
    /// afterwards must still be found. An eagerly filtered list makes that config invisible for the
    /// lifetime of the object, which is silent and looks exactly like "the user has no override".
    /// </summary>
    [Fact]
    public void A_launcher_installed_after_construction_is_found()
    {
        using var dir = new TempDir();
        var homes = Homes(dir);

        Assert.Empty(homes.For(EnvironmentSource.Lutris));

        var data = Make(dir, ".local", "share", "lutris");

        Assert.Equal([data], Roots(homes, EnvironmentSource.Lutris));
    }

    /// <summary>
    /// Lutris keeps game configs under its DATA root on a stock install:
    /// ~/.config/lutris does not exist there and the ten games/*.yml files live at
    /// ~/.local/share/lutris/games/. Scanning only the config root finds nothing.
    /// </summary>
    [Fact]
    public void Lutris_is_found_under_the_data_root_as_well_as_the_config_root()
    {
        using var dir = new TempDir();
        var data = Make(dir, ".local", "share", "lutris");

        Assert.Equal([data], Roots(Homes(dir), EnvironmentSource.Lutris));

        var config = Make(dir, ".config", "lutris");

        Assert.Equal([config, data], Roots(Homes(dir), EnvironmentSource.Lutris).Order());
    }

    /// <summary>
    /// Flatpak redirects $HOME, so the same launcher installed as a Flatpak keeps its configuration
    /// somewhere the native lookup never visits. The Flatpak flag is what separates the two rows in
    /// the UI, since their source and detail are otherwise identical.
    /// </summary>
    [Fact]
    public void A_flatpak_launcher_is_found_and_flagged()
    {
        using var dir = new TempDir();
        var flatpak = Make(dir, ".var", "app", "net.lutris.Lutris", "data", "lutris");

        var homes = Homes(dir).For(EnvironmentSource.Lutris);

        Assert.Single(homes);
        Assert.Equal(flatpak, homes[0].Root);
        Assert.True(homes[0].Flatpak);
    }

    /// <summary>
    /// Flatpak Lutris can keep its game configs under the config root instead of the data root,
    /// just as the native variant does. The native test covers both roots; this test covers the
    /// config root explicitly to ensure that path is discovered when it exists, mirroring the
    /// native case where ~/.config/lutris may exist without ~/.local/share/lutris.
    /// </summary>
    [Fact]
    public void Flatpak_lutris_is_found_under_the_config_root()
    {
        using var dir = new TempDir();
        var flatpak = Make(dir, ".var", "app", "net.lutris.Lutris", "config", "lutris");

        var homes = Homes(dir).For(EnvironmentSource.Lutris);

        Assert.Single(homes);
        Assert.Equal(flatpak, homes[0].Root);
        Assert.True(homes[0].Flatpak);
    }

    [Fact]
    public void Native_and_flatpak_installs_are_both_returned()
    {
        using var dir = new TempDir();
        Make(dir, ".config", "heroic");
        Make(dir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic");

        var homes = Homes(dir).For(EnvironmentSource.Heroic);

        Assert.Equal(2, homes.Count);
        Assert.Equal([false, true], homes.Select(h => h.Flatpak).Order());
    }

    [Fact]
    public void Steam_covers_its_symlinked_roots_its_data_root_and_snap()
    {
        using var dir = new TempDir();
        var expected = new[]
        {
            Make(dir, ".steam", "steam"),
            Make(dir, ".steam", "root"),
            Make(dir, ".local", "share", "Steam"),
            Make(dir, "snap", "steam", "common", ".local", "share", "Steam"),
            Make(dir, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        };

        Assert.Equal(expected.Order(), Roots(Homes(dir), EnvironmentSource.Steam).Order());
    }

    /// <summary>
    /// On a normal install <c>~/.steam/steam</c> and <c>~/.steam/root</c> are both symlinks to
    /// <c>&lt;data&gt;/Steam</c> on a stock install, so without resolving before dedup this one
    /// real Steam install would be discovered, walked and reported on three times over. Real
    /// symlinks, so gated for Windows, where creating one needs privilege.
    /// </summary>
    [Fact]
    public void Symlinked_steam_homes_pointing_at_the_same_target_collapse_to_one()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var real = Make(dir, ".local", "share", "Steam");
        Directory.CreateDirectory(Path.Combine(dir.Path, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, ".steam", "steam"), real);
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, ".steam", "root"), real);

        Assert.Single(Homes(dir).For(EnvironmentSource.Steam));
    }

    /// <summary>
    /// The guard against over-collapsing: two Steam homes that are genuinely different directories,
    /// not symlinks to one another, must both survive dedup. Without a real directory case, a
    /// resolve-and-dedup mechanism that collapsed everything to one entry would pass unnoticed.
    /// </summary>
    [Fact]
    public void Two_genuinely_distinct_steam_roots_both_come_back()
    {
        using var dir = new TempDir();
        var expected = new[]
        {
            Make(dir, ".local", "share", "Steam"),
            Make(dir, "snap", "steam", "common", ".local", "share", "Steam"),
        };

        Assert.Equal(expected.Order(), Roots(Homes(dir), EnvironmentSource.Steam).Order());
    }

    [Fact]
    public void Bottles_is_the_inner_bottles_directory_natively_and_under_flatpak()
    {
        using var dir = new TempDir();
        var expected = new[]
        {
            Make(dir, ".local", "share", "bottles", "bottles"),
            Make(dir, ".var", "app", "com.usebottles.bottles", "data", "bottles", "bottles"),
        };

        Assert.Equal(expected.Order(), Roots(Homes(dir), EnvironmentSource.Bottles).Order());
    }

    [Fact]
    public void Plain_wine_covers_the_default_prefix_the_prefix_directory_and_flatpak()
    {
        using var dir = new TempDir();
        var expected = new[]
        {
            Make(dir, ".wine"),
            Make(dir, ".local", "share", "wineprefixes"),
            Make(dir, ".var", "app", "org.winehq.Wine", "data", "wine"),
        };

        Assert.Equal(expected.Order(), Roots(Homes(dir), EnvironmentSource.Wine).Order());
    }

    /// <summary>
    /// $WINEPREFIX is carried separately rather than added to the Wine home list, because it names
    /// a prefix directly while every home is a container the scanner looks INSIDE. Mixing them
    /// makes the scanner enumerate a prefix's own drive_c as if each subdirectory were a prefix.
    /// </summary>
    [Fact]
    public void The_wine_prefix_variable_is_carried_separately()
    {
        using var dir = new TempDir();
        var prefix = Make(dir, "custom-prefix");

        var homes = Homes(dir, wine: prefix);

        Assert.Equal(prefix, homes.WinePrefixEnvironment);
        Assert.DoesNotContain(prefix, Roots(homes, EnvironmentSource.Wine));
    }

    [Fact]
    public void A_blank_wine_prefix_variable_is_no_variable_at_all()
    {
        using var dir = new TempDir();

        Assert.Null(Homes(dir, wine: "   ").WinePrefixEnvironment);
    }
}
