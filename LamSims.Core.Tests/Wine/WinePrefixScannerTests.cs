using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class WinePrefixScannerTests
{
    /// <summary>A valid prefix at the given path. Returns it.</summary>
    internal static string Prefix(string path, string arch = "win64", string user = "playday")
    {
        Directory.CreateDirectory(Path.Combine(path, "drive_c", "users", user));
        Directory.CreateDirectory(Path.Combine(path, "dosdevices"));
        File.WriteAllText(Path.Combine(path, "system.reg"), $"WINE REGISTRY Version 2\n#arch={arch}\n");
        File.WriteAllText(Path.Combine(path, "user.reg"), $"WINE REGISTRY Version 2\n#arch={arch}\n");
        return path;
    }

    private static WinePrefixScanner Scanner(TempDir dir, Notes notes, string? wine = null,
                                             RecordingLogSink? log = null) =>
        new(new LauncherHomes(dir.Path, null, null, wine), "playday", notes,
            log: log ?? new RecordingLogSink());

    [LinuxFact]
    public void The_configured_prefix_is_found_first_and_labelled_with_its_path()
    {
        using var dir = new TempDir();
        var prefix = Prefix(Path.Combine(dir.Path, "chosen"));
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(prefix);

        Assert.Single(found);
        Assert.Equal(EnvironmentSource.Wine, found[0].Environment.Source);
        Assert.Equal(prefix, found[0].Environment.Detail);
    }

    /// <summary>
    /// Both channels, because whitespace must not be mistaken for a path the user named: a
    /// rejection here would reach the user through the note, and the log would carry a line about
    /// a candidate that was never proposed.
    /// </summary>
    [LinuxFact]
    public void A_blank_configured_prefix_is_ignored_rather_than_rejected()
    {
        using var dir = new TempDir();
        var notes = new Notes();
        var log = new RecordingLogSink();

        Assert.Empty(Scanner(dir, notes, log: log).Scan("   "));
        Assert.DoesNotContain(notes.Lines, l => l.Contains("is not a Wine prefix", StringComparison.Ordinal));
        Assert.False(log.Logged("is not a Wine prefix"), string.Join("\n", log.Texts));
    }

    /// <summary>
    /// The exact string, and in the NOTES rather than the log. A path the user typed into the
    /// setting is the one rejection they have to see: every other candidate came from a launcher's
    /// own configuration and its rejection is enumeration noise, but this one is an answer to
    /// something they did.
    /// </summary>
    [LinuxFact]
    public void A_configured_path_that_is_not_a_prefix_is_reported()
    {
        using var dir = new TempDir();
        var notes = new Notes();

        Assert.Empty(Scanner(dir, notes).Scan(dir.Path));
        Assert.Contains($"The Wine prefix setting names '{dir.Path}', which is not a Wine prefix: "
                        + "no system.reg.", notes.Lines);
    }

    /// <summary>
    /// The complement: a prefix the user named that DOES open earns no note. Without this the test
    /// above is satisfied by reporting every configured prefix, valid ones included.
    /// </summary>
    [LinuxFact]
    public void A_configured_path_that_is_a_prefix_is_not_complained_about()
    {
        using var dir = new TempDir();
        var prefix = Prefix(Path.Combine(dir.Path, "chosen"));
        var notes = new Notes();

        Assert.Single(Scanner(dir, notes).Scan(prefix));
        Assert.DoesNotContain(notes.Lines, l => l.Contains("is not a Wine prefix", StringComparison.Ordinal));
    }

    [LinuxFact]
    public void The_wine_prefix_variable_is_scanned()
    {
        using var dir = new TempDir();
        var prefix = Prefix(Path.Combine(dir.Path, "env-prefix"));
        var notes = new Notes();

        var found = Scanner(dir, notes, wine: prefix).Scan(null);

        Assert.Single(found);
        Assert.Equal("$WINEPREFIX", found[0].Environment.Detail);
    }

    [LinuxFact]
    public void The_default_prefix_and_the_prefix_directory_are_both_scanned()
    {
        using var dir = new TempDir();
        Prefix(Path.Combine(dir.Path, ".wine"));
        Prefix(Path.Combine(dir.Path, ".local", "share", "wineprefixes", "sims"));
        var notes = new Notes();

        var found = Scanner(dir, notes).Scan(null);

        Assert.Equal(2, found.Count);
        Assert.Contains("sims", found.Select(p => p.Environment.Detail));
    }

    /// <summary>
    /// ~/.steam/steam IS a symlink to ~/.local/share/Steam on a stock Steam install.
    /// PathIdentity.Canonical is Path.GetFullPath and never resolves a symlink, so canonical-only
    /// dedup shows every Steam prefix twice. This test fails against Canonical alone.
    /// </summary>
    [LinuxFact]
    public void A_prefix_reached_through_a_symlinked_root_appears_once()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var real = Prefix(Path.Combine(dir.Path, ".local", "share", "wineprefixes", "one"));
        Directory.CreateDirectory(Path.Combine(dir.Path, ".var", "app", "org.winehq.Wine", "data"));
        Directory.CreateSymbolicLink(
            Path.Combine(dir.Path, ".var", "app", "org.winehq.Wine", "data", "wine"),
            Path.GetDirectoryName(real)!);
        var notes = new Notes();

        Assert.Single(Scanner(dir, notes).Scan(null));
    }

    /// <summary>
    /// Several configs routinely name one prefix — twelve hits across at least four game files name
    /// the measured EA app prefix — so a name taken from one of them is non-deterministic. The label
    /// says the launcher and the count and never picks a game.
    /// </summary>
    [LinuxFact]
    public void A_prefix_named_by_several_configs_is_labelled_with_the_count()
    {
        using var dir = new TempDir();
        var prefix = Prefix(Path.Combine(dir.Path, "shared"));
        var notes = new Notes();
        var scanner = Scanner(dir, notes);

        var found = scanner.Describe([
            new PrefixCandidate(prefix, EnvironmentSource.Lutris, "need-for-speed-unbound", false),
            new PrefixCandidate(prefix, EnvironmentSource.Lutris, "ea-app", false),
            new PrefixCandidate(prefix, EnvironmentSource.Lutris, "nfs-palace", false),
        ]);

        Assert.Single(found);
        Assert.Equal("3 games", found[0].Environment.Detail);
        Assert.DoesNotContain("need-for-speed", found[0].Environment.Detail);
    }

    /// <summary>
    /// Two prefixes whose paths differ only in case are two prefixes on a Linux filesystem, and
    /// each has its own user.reg to override and its own record to undo. Deduplicated together,
    /// only the first ever became a target: the second could be neither installed into nor removed
    /// from, and the survivor was mislabelled as shared by "2 games". The label is asserted as well
    /// as the count, because a Describe that returned two rows but still grouped them would report
    /// the wrong detail on both.
    /// </summary>
    [LinuxFact]
    public void Two_prefixes_that_differ_only_in_case_are_both_discovered()
    {
        using var dir = new TempDir();
        var upper = Prefix(Path.Combine(dir.Path, "Prefix"));
        var lower = Prefix(Path.Combine(dir.Path, "prefix"));
        var notes = new Notes();

        var found = Scanner(dir, notes).Describe([
            new PrefixCandidate(upper, EnvironmentSource.Wine, "upper", false),
            new PrefixCandidate(lower, EnvironmentSource.Wine, "lower", false),
        ]);

        var roots = found.Select(f => f.Root).ToList();

        Assert.Equal(2, found.Count);
        Assert.Contains(upper, roots);
        Assert.Contains(lower, roots);
        Assert.DoesNotContain(found, f => f.Environment.Detail.Contains("games"));
    }

    [LinuxFact]
    public void A_prefix_named_once_keeps_its_own_label()
    {
        using var dir = new TempDir();
        var prefix = Prefix(Path.Combine(dir.Path, "one"));
        var notes = new Notes();

        var found = Scanner(dir, notes).Describe(
            [new PrefixCandidate(prefix, EnvironmentSource.Lutris, "ea-app", true)]);

        Assert.Equal("ea-app", found[0].Environment.Detail);
        Assert.True(found[0].Environment.Flatpak);
    }

    /// <summary>
    /// Never an empty string: a row reading "Lutris, " tells the user nothing about which prefix
    /// they are about to write to.
    /// </summary>
    [LinuxFact]
    public void A_candidate_with_no_label_falls_back_to_the_directory_name()
    {
        using var dir = new TempDir();
        var prefix = Prefix(Path.Combine(dir.Path, "fallback-name"));
        var notes = new Notes();

        var found = Scanner(dir, notes).Describe(
            [new PrefixCandidate(prefix, EnvironmentSource.Bottles, "", false)]);

        Assert.Equal("fallback-name", found[0].Environment.Detail);
    }

    /// <summary>
    /// A sandboxed build cannot see host paths without --filesystem=home, which otherwise reads
    /// identically to "you have no prefixes". Emitted only when nothing was found, so a working
    /// sandboxed install does not nag.
    /// </summary>
    [LinuxFact]
    public void A_sandboxed_build_that_finds_nothing_says_so()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".flatpak-info"), "[Application]\n");
        var notes = new Notes();

        new WinePrefixScanner(new LauncherHomes(dir.Path, null, null, null), "playday", notes,
                              flatpakInfoFile: Path.Combine(dir.Path, ".flatpak-info")).Scan(null);

        Assert.True(notes.Any("Flatpak sandbox"), string.Join("\n", notes.Lines));
    }

    /// <summary>
    /// The non-Flatpak half of the same zero-result diagnostic: without it, a Linux user whose
    /// client lives somewhere none of the five launcher sources look sees an empty DLC Unlocker
    /// section, two greyed-out buttons and no explanation at all, when the reason should land in
    /// DetectionNotes. Names the remedy — the Wine-prefix setting — rather than
    /// merely saying nothing was found.
    /// </summary>
    [LinuxFact]
    public void A_linux_build_that_finds_nothing_names_the_wine_prefix_setting()
    {
        using var dir = new TempDir();
        var notes = new Notes();

        Scanner(dir, notes).Scan(null);

        Assert.True(notes.Any("Wine prefix setting"), string.Join("\n", notes.Lines));
    }

    [LinuxFact]
    public void A_line_value_is_read_after_its_key_and_unquoted()
    {
        using var dir = new TempDir();
        dir.Write("game.yml", "name: EA app\nprefix: /mnt/games/foo\nrunner: wine\n");

        Assert.Equal("/mnt/games/foo",
                     WinePrefixScanner.ReadLineValue(dir.File("game.yml"), "prefix"));
        Assert.Null(WinePrefixScanner.ReadLineValue(dir.File("game.yml"), "nothing"));
    }

    /// <summary>
    /// Quotes, inline comments and indentation all occur in real launcher configs. Extract loosely:
    /// a wrong extraction is harmless because validation rejects it with a reason.
    /// </summary>
    [LinuxTheory]
    [InlineData("prefix: \"/mnt/a b\"\n", "/mnt/a b")]
    [InlineData("  prefix: '/mnt/c'\n", "/mnt/c")]
    [InlineData("prefix: /mnt/d   # the one\n", "/mnt/d")]
    [InlineData("prefix:\n", null)]
    public void Line_values_survive_quoting_and_comments(string contents, string? expected)
    {
        using var dir = new TempDir();
        dir.Write("game.yml", contents);

        Assert.Equal(expected, WinePrefixScanner.ReadLineValue(dir.File("game.yml"), "prefix"));
    }

    /// <summary>
    /// Two candidates spelled <root> and <root>/pfx group separately when pfx is a real directory
    /// rather than a link (e.g. Valve Proton's compatdata/<id>/pfx). WinePrefix.TryOpen's
    /// Valve-Proton descent normalises both to Root <root>/pfx, so without the seen.Add guard
    /// that one prefix appears twice. The guard is load-bearing.
    /// </summary>
    [LinuxFact]
    public void A_container_and_its_pfx_named_separately_yield_one_prefix()
    {
        using var dir = new TempDir();
        var container = Path.Combine(dir.Path, "container");
        var pfxDir = Path.Combine(container, "pfx");

        // Create the container directory, but not a valid prefix.
        Directory.CreateDirectory(container);
        Directory.CreateDirectory(Path.Combine(container, "dosdevices"));
        File.WriteAllText(Path.Combine(container, "system.reg"),
                         "WINE REGISTRY Version 2\n#arch=win64\n");
        // No drive_c, so container is not a valid prefix (MissingPart requires system.reg AND
        // drive_c; user.reg is not checked at all).

        // Create the pfx subdirectory as a valid prefix.
        Prefix(pfxDir);

        var notes = new Notes();
        var scanner = Scanner(dir, notes);

        var found = scanner.Describe([
            new PrefixCandidate(container, EnvironmentSource.Wine, "container", false),
            new PrefixCandidate(pfxDir, EnvironmentSource.Wine, "pfx", false),
        ]);

        // Without the seen.Add guard, this would return 2 identical prefixes with different
        // details ("container" and "pfx"). With the guard, exactly 1 prefix comes back, opened
        // from the container path but descended into pfx.
        Assert.Single(found);
        Assert.Equal(pfxDir, found[0].Root);
    }

    /// <summary>
    /// Resolve must walk from the root and RESTART the entire pass on each link substitution.
    /// A single-pass walk that substitutes a link and continues with the target fails when the
    /// target's own path contains intermediate components that are themselves links.
    ///
    /// Concrete case: dir/link1 → dir/mid/end (target is multi-segment), where dir/mid is
    /// itself a link. The single-pass walk finds dir/link1 and substitutes it to dir/mid/end,
    /// then continues walking dir/mid/end/extra — but it never goes back to re-check dir/mid
    /// for a link. Restarting the pass from the root after each substitution fixes this: the
    /// second pass finds dir/mid is a link and substitutes it, then restarts again, and so on.
    ///
    /// This is load-bearing: without the restart, prefixes reached through nested symlinks
    /// appear twice, recreating the exact defect deduplication exists to prevent.
    /// </summary>
    [LinuxFact]
    public void A_prefix_behind_a_symlink_whose_target_crosses_another_symlink_appears_once()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();

        // Create the real prefix at real/prefix.
        var realPath = Path.Combine(dir.Path, "real");
        var prefixPath = Path.Combine(realPath, "prefix");
        Prefix(prefixPath);

        // Create dir/mid → ../../real (a symlink to a multi-segment path).
        var dirPath = Path.Combine(dir.Path, "dir");
        Directory.CreateDirectory(dirPath);
        var midPath = Path.Combine(dirPath, "mid");
        Directory.CreateSymbolicLink(midPath, Path.Combine(dirPath, "..", "real"));

        // Create dir/link → mid/prefix (target contains the link dir/mid).
        var linkPath = Path.Combine(dirPath, "link");
        Directory.CreateSymbolicLink(linkPath, Path.Combine(dirPath, "mid", "prefix"));

        var notes = new Notes();
        var scanner = Scanner(dir, notes);

        // Both candidates reach the same prefix: one directly, one through nested links.
        var found = scanner.Describe([
            new PrefixCandidate(linkPath, EnvironmentSource.Wine, "via-nested-link", false),
            new PrefixCandidate(prefixPath, EnvironmentSource.Wine, "direct", false),
        ]);

        Assert.Single(found);
        Assert.Equal(prefixPath, found[0].Root);
    }
}
