using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

/// <summary>
/// Table-driven, because this one predicate decides whether anything is written at all. Every row
/// is a shape Wine accepts; the hostile rows are the ones a two-valued verdict reads as "absent".
/// </summary>
public class OverridePredicateTests
{
    [Theory]
    [InlineData("version=n,b", OverrideVerdict.SuppliesNative)]
    [InlineData("*version=native,builtin", OverrideVerdict.SuppliesNative)]
    [InlineData("version=native", OverrideVerdict.SuppliesNative)]
    [InlineData("version.dll=n,b", OverrideVerdict.SuppliesNative)]
    [InlineData("VERSION=N,B", OverrideVerdict.SuppliesNative)]
    [InlineData("d3d11=n,b;version=n", OverrideVerdict.SuppliesNative)]
    [InlineData("version=b,n", OverrideVerdict.ForcesBuiltin)]
    [InlineData("version=builtin", OverrideVerdict.ForcesBuiltin)]
    [InlineData("d3d11=n,b;version=b", OverrideVerdict.ForcesBuiltin)]
    [InlineData("version=", OverrideVerdict.ForcesBuiltin)]
    // A value of separators alone carries the same "disabled" meaning as an empty one, and leaves
    // no field to read: taking the first field unconditionally throws on these two.
    [InlineData("version=,", OverrideVerdict.ForcesBuiltin)]
    [InlineData("version=,,", OverrideVerdict.ForcesBuiltin)]
    // Wine lets one entry govern several modules at once. Read as a single name, none of these
    // matches "version", so the two governing rows would fall through to Absent and the hostile
    // one would go unwarned.
    [InlineData("comdlg32,version=n,b", OverrideVerdict.SuppliesNative)]
    [InlineData("comdlg32,version=b", OverrideVerdict.ForcesBuiltin)]
    [InlineData("version,d3d11=b", OverrideVerdict.ForcesBuiltin)]
    [InlineData("versioncheck=n,b", OverrideVerdict.Absent)]
    [InlineData("d3d11=n,b", OverrideVerdict.Absent)]
    // The other half of the module-list pair: a list that does not name version must still be
    // Absent, which a match that merely accepts any list would break.
    [InlineData("comdlg32,d3d11=n,b", OverrideVerdict.Absent)]
    [InlineData("", OverrideVerdict.Absent)]
    [InlineData(null, OverrideVerdict.Absent)]
    public void The_predicate_classifies_every_shape(string? overrides, OverrideVerdict expected)
    {
        Assert.Equal(expected, LauncherOverrides.Classify(overrides));
    }

    /// <summary>
    /// versioncheck must not match. A substring test would classify an unrelated module's override
    /// as ours and skip the registry write that makes the unlocker load.
    /// </summary>
    [Fact]
    public void A_module_whose_name_merely_contains_version_does_not_match()
    {
        Assert.Equal(OverrideVerdict.Absent, LauncherOverrides.Classify("versioncheck=b,n"));
        Assert.Equal(OverrideVerdict.Absent, LauncherOverrides.Classify("myversion=b"));
    }

    /// <summary>
    /// An empty value means "disabled" in Wine, so the DLL is not loaded at all and the unlocker
    /// cannot work on that launch path. Reading it as "absent" would report success.
    /// </summary>
    [Fact]
    public void A_disabled_override_is_hostile_rather_than_absent()
    {
        Assert.Equal(OverrideVerdict.ForcesBuiltin, LauncherOverrides.Classify("version=;d3d11=n"));
    }
}

public class LauncherOverridesTests
{
    private static LauncherOverrides Overrides(TempDir dir) =>
        new(new LauncherHomes(dir.Path, null, null, null));

    private static string Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>
    /// The measured shape: two configs supply the override and a third forces the builtin. Hostile
    /// wins, and the warning names the file and the game so the user can find and fix it.
    /// </summary>
    [Fact]
    public void One_hostile_config_among_several_makes_the_prefix_hostile()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "ea-app"));
        var games = Path.Combine(dir.Path, ".local", "share", "lutris", "games");
        Write(Path.Combine(games, "a.yml"), $"name: EA app\nprefix: {prefix}\nwine:\n  overrides:\n    version.dll: n,b\n");
        Write(Path.Combine(games, "b.yml"), $"name: NFS Unbound\nprefix: {prefix}\nwine:\n  overrides:\n    version.dll: b,n\n");

        var finding = Overrides(dir).For(prefix);

        Assert.Equal(OverrideVerdict.ForcesBuiltin, finding.Verdict);
        Assert.Single(finding.Warnings);
        Assert.Contains("b.yml", finding.Warnings[0]);
        Assert.Contains("NFS Unbound", finding.Warnings[0]);
        Assert.Contains("forces Wine's own version.dll", finding.Warnings[0]);
    }

    /// <summary>
    /// Every config supplying it means no registry write is needed, which is what lets the install
    /// proceed with the prefix live. Asserting only the hostile case would pass against an
    /// implementation that never reported SuppliesNative at all, which turns every ordinary
    /// reinstall into a refusal.
    /// </summary>
    [Fact]
    public void Every_config_supplying_the_override_needs_no_write()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "ea-app"));
        var games = Path.Combine(dir.Path, ".local", "share", "lutris", "games");
        Write(Path.Combine(games, "a.yml"), $"prefix: {prefix}\nwine:\n  overrides:\n    version.dll: n,b\n");
        Write(Path.Combine(games, "b.yml"), $"prefix: {prefix}\nsystem:\n  env:\n    WINEDLLOVERRIDES: version=native,builtin\n");

        var finding = Overrides(dir).For(prefix);

        Assert.Equal(OverrideVerdict.SuppliesNative, finding.Verdict);
        Assert.Empty(finding.Warnings);
    }

    /// <summary>
    /// Mixed native and absent is NOT "every config supplies it": the config that says nothing is a
    /// launch path with no override, so the registry write is still needed.
    /// </summary>
    [Fact]
    public void One_config_without_the_override_still_needs_a_write()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "ea-app"));
        var games = Path.Combine(dir.Path, ".local", "share", "lutris", "games");
        Write(Path.Combine(games, "a.yml"), $"prefix: {prefix}\nwine:\n  overrides:\n    version.dll: n,b\n");
        Write(Path.Combine(games, "b.yml"), $"prefix: {prefix}\n");

        Assert.Equal(OverrideVerdict.Absent, Overrides(dir).For(prefix).Verdict);
    }

    [Fact]
    public void A_prefix_no_config_names_is_absent()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "lonely"));

        Assert.Equal(OverrideVerdict.Absent, Overrides(dir).For(prefix).Verdict);
    }

    /// <summary>
    /// A Valve-Proton-shaped prefix: the launcher config names the CONTAINER, but
    /// WinePrefix.TryOpen descends one level into "pfx" because the container itself has no
    /// drive_c. Comparing the discovered prefix's root (the descended pfx) against the config's
    /// declared path by lexical equality alone never matches, so verdicts stays empty and the
    /// prefix reads as Absent — the silent false success this predicate exists to prevent, since
    /// the install then writes the registry and reports plain success while this hostile config
    /// goes unreported. No symlink needed, so this runs on every CI leg.
    /// </summary>
    [Fact]
    public void A_config_naming_the_container_still_matches_the_descended_pfx_prefix()
    {
        using var dir = new TempDir();
        var container = Path.Combine(dir.Path, "compatdata", "123");
        var pfxDir = Path.Combine(container, "pfx");
        WinePrefixScannerTests.Prefix(pfxDir);
        Directory.CreateDirectory(container); // No drive_c of its own, so TryOpen descends into pfx.

        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "a.yml"),
              $"name: EA app\nprefix: {container}\nwine:\n  overrides:\n    version.dll: b,n\n");

        var finding = Overrides(dir).For(pfxDir);

        Assert.Equal(OverrideVerdict.ForcesBuiltin, finding.Verdict);
        Assert.Single(finding.Warnings);
    }

    /// <summary>
    /// A config reaches the prefix through a symlinked component — the same shape as
    /// ~/.steam/steam or anything under ~/Games -> /mnt/games on a real machine.
    /// PathIdentity.Canonical alone is lexical and never follows the link, so the config's spelling
    /// and the prefix's own root would disagree and the hostile override below would go
    /// unreported. Real symlink, so gated for Windows.
    /// </summary>
    [Fact]
    public void A_config_reaching_the_prefix_through_a_symlinked_component_is_matched()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real");
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(real, "ea-app"));
        var linked = Path.Combine(dir.Path, "linked");
        Directory.CreateSymbolicLink(linked, real);

        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "a.yml"),
              $"name: EA app\nprefix: {Path.Combine(linked, "ea-app")}\n"
              + "wine:\n  overrides:\n    version.dll: b,n\n");

        var finding = Overrides(dir).For(prefix);

        Assert.Equal(OverrideVerdict.ForcesBuiltin, finding.Verdict);
        Assert.Single(finding.Warnings);
    }

    /// <summary>The Lutris env form, which sets WINEDLLOVERRIDES rather than the overrides map.</summary>
    [Fact]
    public void A_lutris_system_env_override_is_read()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "a.yml"),
              $"prefix: {prefix}\nsystem:\n  env:\n    WINEDLLOVERRIDES: version.dll=b,n\n");

        Assert.Equal(OverrideVerdict.ForcesBuiltin, Overrides(dir).For(prefix).Verdict);
    }

    /// <summary>
    /// A Lutris game config's top-level `version:` key is the WINE RUNNER BUILD, not a DLL override.
    /// Reading it as one classifies `version: lutris-GE-Proton8-26` as hostile — its first token
    /// does not begin with 'n' — and reports a forced builtin on most Lutris games, warning about a
    /// config the user never wrote. This is the test that catches that: the only `version` here is a
    /// runner build, so the verdict must be Absent with no warning.
    /// </summary>
    [Fact]
    public void A_lutris_runner_version_is_not_read_as_a_dll_override()
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".local", "share", "lutris", "games", "a.yml"),
              $"name: EA app\nprefix: {prefix}\nrunner: wine\nversion: lutris-GE-Proton8-26\n");

        var finding = Overrides(dir).For(prefix);

        Assert.Equal(OverrideVerdict.Absent, finding.Verdict);
        Assert.Empty(finding.Warnings);
    }

    /// <summary>
    /// The app id IS the compatdata directory name, which is how a localconfig entry is matched to
    /// a prefix without any extra plumbing.
    /// </summary>
    [Fact]
    public void A_steam_launch_option_is_read_for_the_matching_app_id()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, ".local", "share", "Steam");
        var prefix = WinePrefixScannerTests.Prefix(
            Path.Combine(root, "steamapps", "compatdata", "1222670", "pfx"));
        Write(Path.Combine(root, "userdata", "12345", "config", "localconfig.vdf"),
              "\"UserLocalConfigStore\"\n{\n\t\"apps\"\n\t{\n\t\t\"1222670\"\n\t\t{\n"
              + "\t\t\t\"LaunchOptions\"\t\t\"WINEDLLOVERRIDES=\\\"version=b,n\\\" %command%\"\n"
              + "\t\t}\n\t}\n}\n");

        var finding = Overrides(dir).For(prefix);

        Assert.Equal(OverrideVerdict.ForcesBuiltin, finding.Verdict);
        Assert.Contains("1222670", finding.Warnings[0]);
    }

    /// <summary>
    /// A Steam app block may contain nested sibling objects before the LaunchOptions line.
    /// Without brace-depth tracking, a nested object's closing brace would flip inApp false
    /// early, leaving LaunchOptions unread and reporting Absent — a silent failure where the
    /// unlocker silently does not load on that launch path.
    /// </summary>
    [Fact]
    public void A_steam_launch_option_nested_before_it_is_still_read()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, ".local", "share", "Steam");
        var prefix = WinePrefixScannerTests.Prefix(
            Path.Combine(root, "steamapps", "compatdata", "1222670", "pfx"));
        Write(Path.Combine(root, "userdata", "12345", "config", "localconfig.vdf"),
              "\"UserLocalConfigStore\"\n{\n\t\"apps\"\n\t{\n\t\t\"1222670\"\n\t\t{\n"
              + "\t\t\t\"SomeNestedObject\"\n\t\t\t{\n\t\t\t\t\"key\"\t\t\"value\"\n\t\t\t}\n"
              + "\t\t\t\"LaunchOptions\"\t\t\"WINEDLLOVERRIDES=\\\"version=b,n\\\" %command%\"\n"
              + "\t\t}\n\t}\n}\n");

        var finding = Overrides(dir).For(prefix);

        Assert.Equal(OverrideVerdict.ForcesBuiltin, finding.Verdict);
        Assert.Single(finding.Warnings);
    }

    /// <summary>
    /// Non-Steam shortcuts keep launch options in the BINARY shortcuts.vdf, which is out of scope.
    /// Reported as unread rather than ignored: a user whose hostile override lives there would
    /// otherwise be told everything is fine.
    /// </summary>
    [Fact]
    public void A_binary_shortcuts_file_is_reported_as_unread()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, ".local", "share", "Steam");
        var prefix = WinePrefixScannerTests.Prefix(
            Path.Combine(root, "steamapps", "compatdata", "1222670", "pfx"));
        Write(Path.Combine(root, "userdata", "12345", "config", "shortcuts.vdf"), "\0binary\0");

        var finding = Overrides(dir).For(prefix);

        Assert.Single(finding.Unread);
        Assert.Contains("shortcuts.vdf", finding.Unread[0]);
    }

    /// <summary>
    /// Every other read failure in this class is reported into Unread — HeroicSources does,
    /// Steam's LaunchOptions does, even the binary shortcuts.vdf above is. Files() gates the WHOLE
    /// Lutris source, so a failure swallowed there returns verdict Absent with zero warnings while a
    /// hostile config sits unread: a permission problem on ~/.local/share/lutris/games has to reach
    /// Unread. Mode-gated: the failure is arranged with a POSIX mode.
    /// </summary>
    [PosixDenialFact]
    public void An_unreadable_lutris_directory_is_reported_rather_than_silently_empty()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "ea-app"));
        var games = Path.Combine(dir.Path, ".local", "share", "lutris", "games");
        Directory.CreateDirectory(games);

        var unlocked = File.GetUnixFileMode(games);
        File.SetUnixFileMode(games, UnixFileMode.None);

        try
        {
            var finding = Overrides(dir).For(prefix);

            Assert.Equal(OverrideVerdict.Absent, finding.Verdict);
            Assert.Single(finding.Unread);
            Assert.Contains("Lutris", finding.Unread[0]);
        }
        finally
        {
            File.SetUnixFileMode(games, unlocked);
        }
    }

    /// <summary>
    /// "enviromentOptions" is the misspelling Heroic actually ships and it is present in every file
    /// on the measured machine; "environmentOptions" is in none. Reading only the correct spelling
    /// finds nothing, on every Heroic install.
    /// </summary>
    [Theory]
    [InlineData("enviromentOptions")]
    [InlineData("environmentOptions")]
    public void Both_heroic_spellings_are_read(string property)
    {
        using var dir = new TempDir();
        var prefix = WinePrefixScannerTests.Prefix(Path.Combine(dir.Path, "p"));
        Write(Path.Combine(dir.Path, ".config", "heroic", "GamesConfig", "abc.json"),
              $"{{\"abc\":{{\"title\":\"The Sims 4\",\"winePrefix\":{System.Text.Json.JsonSerializer.Serialize(prefix)},"
              + $"\"{property}\":[{{\"key\":\"WINEDLLOVERRIDES\",\"value\":\"version=b,n\"}}]}}}}");

        var finding = Overrides(dir).For(prefix);

        Assert.Equal(OverrideVerdict.ForcesBuiltin, finding.Verdict);
        Assert.Contains("The Sims 4", finding.Warnings[0]);
    }

    [Fact]
    public void A_bottles_dll_override_mapping_is_read()
    {
        using var dir = new TempDir();
        var bottle = WinePrefixScannerTests.Prefix(
            Path.Combine(dir.Path, ".local", "share", "bottles", "bottles", "EA"));
        File.WriteAllText(Path.Combine(bottle, "bottle.yml"),
                          "Name: EA app\nDLL_Overrides:\n  version: n,b\n  d3d11: n\n");

        Assert.Equal(OverrideVerdict.SuppliesNative, Overrides(dir).For(bottle).Verdict);
    }

    /// <summary>
    /// A bottle.yml may carry a top-level `version:` key for the schema version (e.g., `version: 0.1`),
    /// which is not a DLL override. The DLL_Overrides section carries the actual overrides. Without
    /// section-aware reading, a schema key would be misread as an override, classifying it
    /// ForcesBuiltin (since "0" doesn't start with "n") and warning about a hostile override the
    /// user never wrote.
    /// </summary>
    [Fact]
    public void A_bottles_schema_version_is_not_read_as_a_dll_override()
    {
        using var dir = new TempDir();
        var bottle = WinePrefixScannerTests.Prefix(
            Path.Combine(dir.Path, ".local", "share", "bottles", "bottles", "EA"));
        File.WriteAllText(Path.Combine(bottle, "bottle.yml"),
                          "version: 0.1\nName: EA app\nDLL_Overrides:\n  version: n,b\n");

        var finding = Overrides(dir).For(bottle);

        Assert.Equal(OverrideVerdict.SuppliesNative, finding.Verdict);
        Assert.Empty(finding.Warnings);
    }

    /// <summary>
    /// A per-application entry silently outranks the global block, and winecfg's per-application
    /// tab is how a user gets one. Read and reported, never written.
    /// </summary>
    [Fact]
    public void An_app_defaults_entry_is_reported_as_a_conflict()
    {
        using var f = new PrefixFixture();
        f.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                       + "[Software\\\\Wine\\\\AppDefaults\\\\EADesktop.exe\\\\DllOverrides] 0\n"
                       + "\"*version\"=\"builtin\"\n");

        var conflict = LauncherOverrides.AppDefaultsConflict(f.Open(), "EADesktop.exe");

        Assert.NotNull(conflict);
        Assert.Contains("EADesktop.exe", conflict);
        Assert.Contains("takes precedence", conflict);
    }

    [Fact]
    public void No_app_defaults_entry_is_no_conflict()
    {
        using var f = new PrefixFixture();

        Assert.Null(LauncherOverrides.AppDefaultsConflict(f.Open(), "EADesktop.exe"));
    }
}
