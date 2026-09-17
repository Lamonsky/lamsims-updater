using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Logging;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class WinePrefixOpenTests
{
    private static TargetEnvironment Env(string detail = "test") =>
        new(EnvironmentSource.Wine, detail);

    [LinuxFact]
    public void A_directory_with_system_reg_and_drive_c_opens()
    {
        using var f = new PrefixFixture();

        var prefix = WinePrefix.TryOpen(f.Root, Env(), "playday");

        Assert.NotNull(prefix);
        Assert.Equal(PathIdentity.Canonical(f.Root), PathIdentity.Canonical(prefix.Root));
        Assert.Equal(PathIdentity.Canonical(f.DriveC), PathIdentity.Canonical(prefix.DriveC));
    }

    /// <summary>
    /// The exact string, naming WHICH file is missing. A generic "not a prefix" would
    /// leave a user with a wrong Lutris path unable to tell it apart from a permissions problem.
    ///
    /// Both channels are asserted. The scanner probes every path five launchers mention, so this
    /// line is written about directories nobody claimed were prefixes and belongs in the log; the
    /// notes channel is rendered in the window, where thirty of these buried the controls.
    /// </summary>
    [LinuxFact]
    public void A_directory_that_is_not_a_prefix_is_rejected_with_the_missing_file_named()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();

        Assert.Null(WinePrefix.TryOpen(dir.Path, Env(), "playday", log));

        Assert.Contains($"'{dir.Path}' is not a Wine prefix: no system.reg.", log.Texts);
    }

    [LinuxFact]
    public void A_prefix_with_system_reg_but_no_drive_c_names_drive_c()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "system.reg"), "#arch=win64\n");
        var log = new RecordingLogSink();

        Assert.Null(WinePrefix.TryOpen(dir.Path, Env(), "playday", log));

        Assert.Contains($"'{dir.Path}' is not a Wine prefix: no drive_c.", log.Texts);
    }

    /// <summary>
    /// The explainer the configured-prefix note is built from, checked against TryOpen's own
    /// verdict on the same three shapes. They share one helper precisely so a user cannot be told
    /// their prefix is fine while the scan rejects it.
    /// </summary>
    [LinuxFact]
    public void The_reason_a_path_is_not_a_prefix_matches_what_opening_it_decides()
    {
        using var missingReg = new TempDir();
        Assert.Equal("no system.reg", WinePrefix.WhyNotAPrefix(missingReg.Path));

        using var missingDriveC = new TempDir();
        File.WriteAllText(Path.Combine(missingDriveC.Path, "system.reg"), "#arch=win64\n");
        Assert.Equal("no drive_c", WinePrefix.WhyNotAPrefix(missingDriveC.Path));

        using var real = new PrefixFixture();
        Assert.Null(WinePrefix.WhyNotAPrefix(real.Root));
        Assert.NotNull(WinePrefix.TryOpen(real.Root, Env(), "playday"));
    }

    /// <summary>
    /// Valve Proton keeps the prefix in a real `pfx/` subdirectory while the launcher config
    /// records the container. Without the one-shot retry every Valve-Proton-shaped Lutris, Heroic
    /// or Bottles prefix is rejected.
    /// </summary>
    [LinuxFact]
    public void A_container_holding_pfx_opens_as_that_pfx()
    {
        using var dir = new TempDir();
        var container = Path.Combine(dir.Path, "compatdata", "1262600");
        var inner = Path.Combine(container, "pfx");
        Directory.CreateDirectory(Path.Combine(inner, "drive_c"));
        File.WriteAllText(Path.Combine(inner, "system.reg"), "#arch=win64\n");

        var prefix = WinePrefix.TryOpen(container, Env(), "steamuser");

        Assert.NotNull(prefix);
        Assert.Equal(PathIdentity.Canonical(inner), PathIdentity.Canonical(prefix.Root));
    }

    /// <summary>
    /// The retry is ONE level. A container two levels above a prefix is a rejection, not a search:
    /// Heroic's defaultSettings.winePrefix is a container the scanner enumerates, and
    /// a recursive retry here would make that enumeration redundant and find prefixes nobody
    /// configured.
    /// </summary>
    [LinuxFact]
    public void The_pfx_retry_does_not_recurse()
    {
        using var dir = new TempDir();
        var deep = Path.Combine(dir.Path, "a", "pfx", "pfx");
        Directory.CreateDirectory(Path.Combine(deep, "drive_c"));
        File.WriteAllText(Path.Combine(deep, "system.reg"), "#arch=win64\n");

        Assert.Null(WinePrefix.TryOpen(Path.Combine(dir.Path, "a"), Env(), "playday"));
    }

    [LinuxTheory]
    [InlineData("win64", WineArch.Win64)]
    [InlineData("win32", WineArch.Win32)]
    public void The_arch_comes_from_the_reg_header(string written, WineArch expected)
    {
        using var f = new PrefixFixture(arch: written);

        Assert.Equal(expected, f.Open().Arch);
    }

    /// <summary>
    /// Treated as win64 with a diagnostic, never as a rejection. A prefix whose header
    /// we cannot read still holds a client we can unlock.
    /// </summary>
    [LinuxFact]
    public void A_missing_arch_line_is_win64_with_a_note()
    {
        using var f = new PrefixFixture();
        f.WriteSystemReg("WINE REGISTRY Version 2\n");
        f.WriteUserReg("WINE REGISTRY Version 2\n");
        var log = new RecordingLogSink();

        Assert.Equal(WineArch.Win64, f.Open(log).Arch);
        Assert.True(log.Logged("architecture"), string.Join("\n", log.Texts));
    }

    /// <summary>
    /// GE-Proton and umu write the marker at the prefix root; Valve Proton writes it one level up,
    /// beside `pfx`. Checking only the root misses every Valve Proton prefix, and the reader then
    /// picks the wrong Windows user for exactly the population that always uses `steamuser`.
    /// </summary>
    [LinuxFact]
    public void A_proton_marker_beside_pfx_is_seen()
    {
        using var dir = new TempDir();
        var container = Path.Combine(dir.Path, "compatdata", "1262600");
        var inner = Path.Combine(container, "pfx");
        Directory.CreateDirectory(Path.Combine(inner, "drive_c", "users", "steamuser"));
        File.WriteAllText(Path.Combine(inner, "system.reg"), "#arch=win64\n");
        File.WriteAllText(Path.Combine(container, "config_info"), "GE-Proton10-34\n");

        var prefix = WinePrefix.TryOpen(container, Env(), "playday");

        Assert.NotNull(prefix);
        Assert.True(prefix.ProtonManaged);
        Assert.Equal("steamuser", Path.GetFileName(prefix.PrimaryUserDirectory));
    }

    [LinuxFact]
    public void A_plain_prefix_uses_the_injected_user_name()
    {
        using var f = new PrefixFixture(user: "playday");
        f.AddUser("steamuser");

        var prefix = f.Open();

        Assert.False(prefix.ProtonManaged);
        Assert.Equal("playday", Path.GetFileName(prefix.PrimaryUserDirectory));
        Assert.Single(prefix.WindowsUserDirectories);
    }

    /// <summary>
    /// The pair. Asserting only the count would pass against a list that included `Public`, whose
    /// AppData the unlocker DLL never reads, and asserting only the names would pass against an
    /// implementation that returned every user for every prefix.
    /// </summary>
    [LinuxFact]
    public void An_unrecognisable_user_writes_to_every_non_public_user_with_a_note()
    {
        using var f = new PrefixFixture(user: "nobody-here");
        Directory.Delete(Path.Combine(f.DriveC, "users", "nobody-here"), recursive: true);
        f.AddUser("steamuser");
        f.AddUser("crossover");
        var log = new RecordingLogSink();

        var prefix = f.Open(log);

        Assert.Equal(["crossover", "steamuser"],
                     prefix.WindowsUserDirectories.Select(Path.GetFileName).Order());
        Assert.True(log.Logged("has more than one Windows user; the configuration will be written under each.",
                               LogSeverity.Warning),
                    string.Join("\n", log.Texts));
    }
}

public class WinePrefixResolutionTests
{
    /// <summary>
    /// The dosdevices entry as a real DIRECTORY, so this mechanism is covered on the Windows and
    /// macOS CI legs where creating a symlink needs privilege: without it the reader is exercised
    /// on Linux only.
    /// </summary>
    [LinuxFact]
    public void A_drive_entry_that_is_a_real_directory_resolves()
    {
        using var f = new PrefixFixture();
        var target = Path.Combine(f.Dir.Path, "expansion");
        Directory.CreateDirectory(Path.Combine(target, "Games"));
        f.RealDrive('d', target);

        var resolved = f.Open().ResolveWindowsPath(@"D:\Games");

        Assert.NotNull(resolved);
        Assert.True(Directory.Exists(resolved));
        Assert.Equal("Games", Path.GetFileName(resolved));
    }

    /// <summary>Linux-gated: symlink creation needs privilege on Windows.</summary>
    [LinuxFact]
    public void A_drive_symlink_to_a_target_outside_the_prefix_resolves()
    {
        if (OperatingSystem.IsWindows()) return;

        using var f = new PrefixFixture();
        var target = Path.Combine(f.Dir.Path, "mnt-expansion");
        Directory.CreateDirectory(Path.Combine(target, "Games"));
        f.LinkDrive('d', target);

        var resolved = f.Open().ResolveWindowsPath(@"D:\Games");

        Assert.Equal(PathIdentity.Canonical(Path.Combine(target, "Games")),
                     PathIdentity.Canonical(resolved));
    }

    /// <summary>
    /// The pair. `d::` is a block device and only two-character names are drives, but asserting
    /// merely that `d::` is IGNORED passes against an implementation that never reads dosdevices
    /// at all and always falls back to drive_c — so the same test asserts where `D:\x` actually
    /// lands.
    /// </summary>
    [LinuxFact]
    public void A_block_device_entry_is_not_a_drive()
    {
        using var f = new PrefixFixture();
        var drive = Path.Combine(f.Dir.Path, "real-d");
        Directory.CreateDirectory(Path.Combine(drive, "x"));
        f.RealDrive('d', drive);
        Directory.CreateDirectory(Path.Combine(f.Root, "dosdevices", "d::"));

        var resolved = f.Open().ResolveWindowsPath(@"D:\x");

        Assert.NotNull(resolved);
        Assert.Equal(PathIdentity.Canonical(Path.Combine(f.Root, "dosdevices", "d:", "x")),
                     PathIdentity.Canonical(resolved));
    }

    /// <summary>
    /// Every dosdevices entry is lowercase and ext4 is case-sensitive, so without lowercasing the
    /// letter this fails on every prefix, for every path, on a real Wine install.
    /// </summary>
    [LinuxFact]
    public void An_uppercase_drive_letter_resolves()
    {
        using var f = new PrefixFixture();
        Directory.CreateDirectory(Path.Combine(f.DriveC, "windows"));

        Assert.NotNull(f.Open().ResolveWindowsPath(@"C:\windows"));
    }

    /// <summary>
    /// c: falls back to drive_c because that is correct for a damaged prefix and it is what lets
    /// these tests run where symlink creation needs privilege. No OTHER letter falls back: a
    /// missing d: must not silently resolve inside drive_c and install the unlocker in the wrong
    /// place.
    /// </summary>
    [LinuxFact]
    public void A_missing_drive_letter_other_than_c_returns_null()
    {
        using var f = new PrefixFixture();
        Directory.CreateDirectory(Path.Combine(f.DriveC, "Games"));

        var prefix = f.Open();

        Assert.NotNull(prefix.ResolveWindowsPath(@"C:\Games"));
        Assert.Null(prefix.ResolveWindowsPath(@"D:\Games"));
    }

    [LinuxFact]
    public void Segments_match_case_insensitively()
    {
        using var f = new PrefixFixture();
        Directory.CreateDirectory(Path.Combine(f.DriveC, "Program Files", "Electronic Arts"));

        var resolved = f.Open().ResolveWindowsPath(@"C:\PROGRAM FILES\electronic arts");

        Assert.NotNull(resolved);
        Assert.True(Directory.Exists(resolved));
    }

    /// <summary>
    /// The pair that makes rule 6 real: a missing FINAL segment comes back as a join the caller can
    /// create (ProgramData frequently does not exist and the engine's machine.ini step creates it),
    /// while a missing INTERMEDIATE segment is null. One assertion without the other passes against
    /// an implementation that joins blindly and against one that gives up on anything absent.
    /// </summary>
    [LinuxFact]
    public void A_missing_final_segment_joins_and_a_missing_intermediate_is_null()
    {
        using var f = new PrefixFixture();
        Directory.CreateDirectory(Path.Combine(f.DriveC, "ProgramData"));
        var prefix = f.Open();

        var final = prefix.ResolveWindowsPath(@"C:\ProgramData\EA Desktop");

        Assert.Equal(PathIdentity.Canonical(Path.Combine(f.DriveC, "ProgramData", "EA Desktop")),
                     PathIdentity.Canonical(final));
        Assert.False(Directory.Exists(final));
        Assert.Null(prefix.ResolveWindowsPath(@"C:\ProgramData\Nope\EA Desktop"));
    }

    /// <summary>
    /// A host that returns the raw registry value makes detection find nothing on every prefix
    /// with no error, because
    /// Path.GetDirectoryName of a backslash path returns an empty string on Linux. So the assertion
    /// is not "it resolved" but "what the ENGINE then does with it works".
    /// </summary>
    [LinuxFact]
    public void A_resolved_client_path_survives_the_engines_directory_split()
    {
        using var f = new PrefixFixture();
        const string windows = @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe";
        var expected = f.AddClient(ClientKind.EaApp, windows);

        var resolved = f.Open().ResolveWindowsPath(windows);

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
        Assert.Equal(PathIdentity.Canonical(expected),
                     PathIdentity.Canonical(Path.GetDirectoryName(resolved)));
        Assert.NotEqual("", Path.GetDirectoryName(resolved));
    }

    /// <summary>
    /// Both UnlockerPaths roots go through resolution rather than a string join, and they
    /// are the two paths most likely to be silently wrong. Roaming must exist; ProgramData
    /// frequently does not and must still come back joined so the engine can create it.
    /// </summary>
    [LinuxFact]
    public void PathsFor_resolves_both_roots()
    {
        using var f = new PrefixFixture(user: "playday");
        var prefix = f.Open();

        var paths = prefix.PathsFor(prefix.PrimaryUserDirectory);

        Assert.NotNull(paths);
        Assert.Equal(PathIdentity.Canonical(
                         Path.Combine(f.DriveC, "users", "playday", "AppData", "Roaming")),
                     PathIdentity.Canonical(paths.Roaming));
        Assert.Equal(PathIdentity.Canonical(Path.Combine(f.DriveC, "ProgramData")),
                     PathIdentity.Canonical(paths.CommonAppData));
        Assert.False(Directory.Exists(paths.CommonAppData));
        Assert.StartsWith(paths.Roaming, paths.ConfigDirectory, StringComparison.Ordinal);
    }
}
