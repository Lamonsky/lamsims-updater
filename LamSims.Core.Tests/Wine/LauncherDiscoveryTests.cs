using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Logging;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class LauncherDiscoveryTests
{
    private static WinePrefixScanner Scanner(TempDir dir, Notes notes,
                                             RecordingLogSink? log = null) =>
        new(new LauncherHomes(dir.Path, null, null, null), "playday", notes,
            log: log ?? new RecordingLogSink());

    private static string Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>
    /// A glob over ~/Games cannot find this one: the prefix is nowhere near it, and the only thing
    /// that knows where it is is the game's own YAML.
    /// </summary>
    [LinuxFact]
    public void A_lutris_prefix_on_another_mount_is_found()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "mnt", "games", "foo"));
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "ea-app-1778070803.yml"),
              $"name: EA app\ngame_slug: ea-app\nprefix: {prefix}\n");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Equal(EnvironmentSource.Lutris, found[0].Environment.Source);
    }

    /// <summary>
    /// The in-file name, not the filename. Lutris filenames carry a numeric suffix
    /// (ea-app-1778070803.yml) that means nothing to the user.
    /// </summary>
    [LinuxFact]
    public void A_lutris_prefix_is_labelled_from_the_configs_own_name()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "ea-app-1778070803.yml"),
              $"name: EA app\nprefix: {prefix}\n");
        var notes = new Notes();

        Assert.Equal("EA app", Scanner(dir, notes).Scan(null)[0].Environment.Detail);
    }

    [LinuxFact]
    public void With_no_name_the_lutris_game_slug_is_used()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "x.yml"),
              $"game_slug: ea-app\nprefix: {prefix}\n");
        var notes = new Notes();

        Assert.Equal("ea-app", Scanner(dir, notes).Scan(null)[0].Environment.Detail);
    }

    /// <summary>
    /// Configs under the CONFIG root only. The data-root case is covered above; a stock install has
    /// only the data root, and a user who moved their XDG dirs has only this one, so an
    /// implementation that scans one root fails for half the population.
    /// </summary>
    [LinuxFact]
    public void Lutris_configs_under_the_config_root_are_found()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".config", "lutris", "games", "x.yml"),
              $"name: EA app\nprefix: {prefix}\n");
        var notes = new Notes();

        Assert.Single(Scanner(dir, notes).Scan(null));
    }

    [LinuxFact]
    public void A_flatpak_lutris_is_scanned_and_flagged()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".var", "app", "net.lutris.Lutris", "data", "lutris",
                           "games", "x.yml"),
              $"name: EA app\nprefix: {prefix}\n");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.True(found[0].Environment.Flatpak);
    }

    /// <summary>
    /// Three Lutris configs naming one prefix is the measured reality — and they disagree about the
    /// game. The label must be the launcher and the count.
    /// </summary>
    [LinuxFact]
    public void Three_lutris_configs_naming_one_prefix_produce_one_counted_row()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "ea-app"));
        var games = Path.Combine(dir.Path, ".local", "share", "lutris", "games");
        Write(Path.Combine(games, "a.yml"), $"name: EA app\nprefix: {prefix}\n");
        Write(Path.Combine(games, "b.yml"), $"name: NFS Unbound\nprefix: {prefix}\n");
        Write(Path.Combine(games, "c.yml"), $"name: NFS Palace\nprefix: {prefix}\n");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Equal("3 games", found[0].Environment.Detail);
    }

    [LinuxFact]
    public void A_lutris_config_naming_a_path_that_is_not_a_prefix_is_rejected_with_a_reason()
    {
        using var dir = new TempDir();
        var notReally = Path.Combine(dir.Path, "not-a-prefix");
        Directory.CreateDirectory(notReally);
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "x.yml"),
              $"name: EA app\nprefix: {notReally}\n");
        var notes = new Notes();
        var log = new RecordingLogSink();

        Assert.Empty(Scanner(dir, notes, log).Scan(null));

        // The LOG, and not the notes. Lutris proposed this path; the user did not, and never
        // claimed it was a prefix. Reporting it to the notes put one line in front of the user for
        // every game their launchers happened to mention.
        Assert.True(log.Logged($"'{notReally}' is not a Wine prefix: no system.reg."),
                    string.Join("\n", log.Texts));
        Assert.DoesNotContain(notes.Lines, l => l.Contains("is not a Wine prefix", StringComparison.Ordinal));
    }

    /// <summary>
    /// A single Lutris game YAML containing two distinct prefix: lines, both pointing at valid
    /// prefixes. This covers the unguarded behavior of ReadLineValues, which may produce more than
    /// one hit per config file — a config can carry more than one and taking only the first would
    /// silently drop the rest. This test must remain permanent to catch any future regression.
    /// </summary>
    [LinuxFact]
    public void A_lutris_config_with_two_prefixes_discovers_both()
    {
        using var dir = new TempDir();
        var prefix1 = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p1"));
        var prefix2 = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p2"));
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "game.yml"),
              $"name: Multi-Prefix Game\nprefix: {prefix1}\nprefix: {prefix2}\n");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Equal(2, found.Count);
        Assert.Equal([prefix1, prefix2], found.Select(p => p.Root).Order());
        Assert.True(found.All(f => f.Environment.Source == EnvironmentSource.Lutris));
    }

    [LinuxFact]
    public void A_heroic_games_config_prefix_is_found_and_labelled()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "heroic-prefix"));
        Write(Path.Combine(dir.Path, ".config", "heroic", "GamesConfig", "abc123.json"),
              $"{{\"abc123\":{{\"title\":\"The Sims 4\",\"winePrefix\":{System.Text.Json.JsonSerializer.Serialize(prefix)}}}}}");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Equal(EnvironmentSource.Heroic, found[0].Environment.Source);
        Assert.Equal("The Sims 4", found[0].Environment.Detail);
    }

    [LinuxFact]
    public void With_no_title_the_heroic_config_id_is_the_label()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".config", "heroic", "GamesConfig", "abc123.json"),
              $"{{\"abc123\":{{\"winePrefix\":{System.Text.Json.JsonSerializer.Serialize(prefix)}}}}}");
        var notes = new Notes();

        Assert.Equal("abc123", Scanner(dir, notes).Scan(null)[0].Environment.Detail);
    }

    /// <summary>
    /// defaultSettings.winePrefix is a CONTAINER of one prefix per game, not a prefix: on the
    /// measured machine it is ~/Games/Heroic/Prefixes/default, which has neither system.reg nor
    /// drive_c. Treating it as a prefix guarantees a spurious rejection on every Heroic install,
    /// and never finding the prefixes that are actually inside it.
    /// </summary>
    [LinuxFact]
    public void The_heroic_default_prefix_setting_is_enumerated_one_level()
    {
        using var dir = new TempDir();
        var container = Path.Combine(dir.Path, "Games", "Heroic", "Prefixes", "default");
        WinePrefixScannerTests.Prefix(Path.Combine(container, "TheSims4"));
        WinePrefixScannerTests.Prefix(Path.Combine(container, "Other"));
        Write(Path.Combine(dir.Path, ".config", "heroic", "config.json"),
              $"{{\"defaultSettings\":{{\"winePrefix\":{System.Text.Json.JsonSerializer.Serialize(container)}}}}}");
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Equal(2, found.Count);
        Assert.Equal(["Other", "TheSims4"], found.Select(p => p.Environment.Detail).Order());
        Assert.False(notes.Any(container), string.Join("\n", notes.Lines));
    }

    /// <summary>A bottle IS a prefix, so each directory under the bottles root is a candidate.</summary>
    [LinuxFact]
    public void Each_bottle_is_a_prefix()
    {
        using var dir = new TempDir();
        var bottles = Path.Combine(dir.Path, ".local", "share", "bottles", "bottles");
        WinePrefixScannerTests.Prefix(Path.Combine(bottles, "EA-app"));
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Single(found);
        Assert.Equal(EnvironmentSource.Bottles, found[0].Environment.Source);
        Assert.Equal("EA-app", found[0].Environment.Detail);
    }

    [LinuxFact]
    public void A_bottles_name_from_its_own_yaml_wins_over_the_directory_name()
    {
        using var dir = new TempDir();
        var bottle = WinePrefixScannerTests.Prefix(
            Path.Combine(dir.Path, ".local", "share", "bottles", "bottles", "dir-name"));
        File.WriteAllText(Path.Combine(bottle, "bottle.yml"), "Name: EA app\nArch: win64\n");
        var notes = new Notes();

        Assert.Equal("EA app", Scanner(dir, notes).Scan(null)[0].Environment.Detail);
    }

    /// <summary>
    /// Malformed JSON and an unreadable directory are both ordinary on a machine with several
    /// launchers. Each yields a diagnostic and no candidates, and neither stops the scan: the
    /// prefix from the other launcher must still come back.
    /// </summary>
    [LinuxFact]
    public void A_malformed_config_yields_a_diagnostic_and_does_not_stop_the_scan()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".config", "heroic", "GamesConfig", "broken.json"),
              "{not json at all");
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "x.yml"),
              $"name: EA app\nprefix: {prefix}\n");
        var notes = new Notes();
        var log = new RecordingLogSink();

        var found = Scanner(dir, notes, log).Scan(null);

        Assert.Single(found);
        Assert.True(log.Logged("could not be read"), string.Join("\n", log.Texts));
    }

    /// <summary>
    /// A JSON file with a valid parse but wrong root shape (array instead of object) must not crash
    /// the entire scan. EnumerateObject() throws InvalidOperationException on non-object roots,
    /// which would propagate to Scan and kill discovery for all other sources if not guarded. This
    /// test ensures the guard is present and working: one broken Heroic config does not cost the
    /// user the prefixes from Lutris, Steam, Bottles, or plain Wine.
    /// </summary>
    [LinuxFact]
    public void A_heroic_gamesconfig_with_array_root_does_not_stop_the_scan()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".config", "heroic", "GamesConfig", "bad.json"),
              "[1, 2, 3]");
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "x.yml"),
              $"name: EA app\nprefix: {prefix}\n");
        var notes = new Notes();
        var log = new RecordingLogSink();

        var found = Scanner(dir, notes, log).Scan(null);

        Assert.Single(found);
        Assert.True(log.Logged("root is not a JSON object"), string.Join("\n", log.Texts));
    }

    /// <summary>
    /// The launcher-wide config.json is also guarded: a malformed but parseable root (array
    /// instead of object) must not crash discovery. This test ensures the diagnostic is recorded,
    /// which is the invariant the class' doc comment promises: "An unreadable or malformed config
    /// yields a diagnostic and no candidates, never an exception".
    /// </summary>
    [LinuxFact]
    public void A_heroic_config_json_with_array_root_does_not_stop_the_scan()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".config", "heroic", "config.json"),
              "[1, 2, 3]");
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "x.yml"),
              $"name: EA app\nprefix: {prefix}\n");
        var notes = new Notes();
        var log = new RecordingLogSink();

        var found = Scanner(dir, notes, log).Scan(null);

        Assert.Single(found);
        Assert.True(log.Logged("config.json"), string.Join("\n", log.Texts));
        Assert.True(log.Logged("root is not a JSON object"), string.Join("\n", log.Texts));
    }
}
