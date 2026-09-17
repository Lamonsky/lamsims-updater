using System;
using System.IO;
using Xunit;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class WineProcessesTests
{
    /// <summary>
    /// A fake /proc: numbered directories holding `maps` and `cmdline` as ordinary files. Real
    /// /proc exposes cwd, exe and fd as symlinks, which need privilege to create on Windows, so
    /// the portable sources are exercised everywhere and the symlink ones are gated below.
    /// </summary>
    private static void Process(string procRoot, int pid, string cmdline, string maps = "")
    {
        var dir = Path.Combine(procRoot, pid.ToString());
        Directory.CreateDirectory(dir);

        // NUL-separated, the way the kernel writes it.
        File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline.Replace('|', '\0'));
        File.WriteAllText(Path.Combine(dir, "maps"), maps);
    }

    private static string Map(string path) =>
        $"7f0000000000-7f0000001000 r--p 00000000 fd:00 1234 {path}\n";

    [LinuxFact]
    public void A_prefix_with_no_process_naming_it_is_idle()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        Directory.CreateDirectory(prefix);
        Process(dir.Path, 100, "/usr/bin/bash", Map("/usr/lib64/libc.so.6"));

        Assert.False(new WineProcesses(dir.Path).IsPrefixLive(prefix));
    }

    /// <summary>
    /// A mapped PE from the prefix's own drive_c. This is the signal that has no hole: a Wine
    /// process running in ~/.wine maps its system32 DLLs whether or not WINEPREFIX is set, and the
    /// unset case is exactly where matching /proc/&lt;pid&gt;/environ reports idle while a server
    /// is live.
    /// </summary>
    [LinuxFact]
    public void A_process_mapping_a_file_inside_the_prefix_makes_it_live()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        Directory.CreateDirectory(prefix);
        Process(dir.Path, 100, "/usr/bin/wine",
                Map(Path.Combine(prefix, "drive_c", "windows", "system32", "kernel32.dll")));

        Assert.True(new WineProcesses(dir.Path).IsPrefixLive(prefix));
    }

    /// <summary>
    /// Full-segment equality after canonicalisation, never StartsWith: ~/.wine must not match
    /// ~/.wine-backup. A user with both would otherwise be told the prefix they are installing into
    /// is busy because of a process in an unrelated one.
    /// </summary>
    [LinuxFact]
    public void A_sibling_directory_with_the_same_prefix_string_does_not_match()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, ".wine");
        Directory.CreateDirectory(prefix);
        Process(dir.Path, 100, "/usr/bin/wine",
                Map(Path.Combine(dir.Path, ".wine-backup", "drive_c", "x.dll")));

        Assert.False(new WineProcesses(dir.Path).IsPrefixLive(prefix));
    }

    /// <summary>
    /// cmdline, never comm. comm is capped at 15 bytes, so EABackgroundService.exe truncates to
    /// EABackgroundSer and a comm-based matcher never sees the client it was written to find.
    /// The name is 23 characters on purpose.
    /// </summary>
    [LinuxFact]
    public void A_client_whose_name_exceeds_the_comm_cap_is_found()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(prefix, "drive_c", "EA", "EABackgroundService.exe");
        Process(dir.Path, 100, $"{exe}|-silent", Map(exe));

        var running = new WineProcesses(dir.Path)
            .RunningClients(prefix, ["EABackgroundService.exe", "EADesktop.exe"]);

        Assert.Equal(["EABackgroundService.exe"], running);
    }

    /// <summary>
    /// argv[0] of a Wine process can be a Windows path, so the last segment has to be taken across
    /// both separators. Splitting on '/' alone leaves the whole string and matches nothing.
    /// </summary>
    [LinuxFact]
    public void A_windows_style_command_line_still_yields_the_executable_name()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        Directory.CreateDirectory(prefix);
        Process(dir.Path, 100, @"C:\Program Files\Electronic Arts\EA Desktop\EADesktop.exe",
                Map(Path.Combine(prefix, "drive_c", "windows", "system32", "kernel32.dll")));

        Assert.Equal(["EADesktop.exe"],
                     new WineProcesses(dir.Path).RunningClients(prefix, ["EADesktop.exe"]));
    }

    /// <summary>
    /// The name matching is not enough on its own: the same client can be running in a DIFFERENT
    /// prefix, and warning the user to restart a client that has nothing to do with this install
    /// is a warning they cannot act on.
    /// </summary>
    [LinuxFact]
    public void A_client_running_in_another_prefix_is_not_reported()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        var other = Path.Combine(dir.Path, "other");
        Directory.CreateDirectory(prefix);
        Directory.CreateDirectory(other);
        Process(dir.Path, 100, Path.Combine(other, "EADesktop.exe"),
                Map(Path.Combine(other, "drive_c", "x.dll")));

        Assert.Empty(new WineProcesses(dir.Path).RunningClients(prefix, ["EADesktop.exe"]));
    }

    [LinuxFact]
    public void A_matching_name_is_reported_once_however_many_processes_carry_it()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(prefix, "EADesktop.exe");
        Process(dir.Path, 100, exe, Map(exe));
        Process(dir.Path, 101, exe, Map(exe));

        Assert.Equal(["EADesktop.exe"],
                     new WineProcesses(dir.Path).RunningClients(prefix, ["EADesktop.exe"]));
    }

    /// <summary>
    /// A non-numeric entry (/proc/self, /proc/meminfo) and a process that exits between the listing
    /// and the read are both ordinary. Neither may throw: liveness is consulted from an install
    /// that has already been told to go ahead.
    /// </summary>
    [LinuxFact]
    public void Unreadable_and_non_numeric_entries_are_skipped()
    {
        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        Directory.CreateDirectory(prefix);
        Directory.CreateDirectory(Path.Combine(dir.Path, "self"));
        File.WriteAllText(Path.Combine(dir.Path, "meminfo"), "MemTotal: 1 kB");
        Directory.CreateDirectory(Path.Combine(dir.Path, "999"));   // no cmdline, no maps

        var processes = new WineProcesses(dir.Path);

        Assert.False(processes.IsPrefixLive(prefix));
        Assert.Empty(processes.RunningClients(prefix, ["EADesktop.exe"]));
    }

    [LinuxFact]
    public void A_missing_proc_root_is_idle_rather_than_a_fault()
    {
        using var dir = new TempDir();

        Assert.False(new WineProcesses(Path.Combine(dir.Path, "nope")).IsPrefixLive(dir.Path));
    }

    /// <summary>
    /// The cwd source, which catches a process that has the prefix as its working directory without
    /// mapping anything from it. Linux-gated: creating a symlink needs privilege on Windows.
    /// </summary>
    [LinuxFact]
    public void A_process_whose_cwd_is_inside_the_prefix_makes_it_live()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        var inside = Path.Combine(prefix, "drive_c");
        Directory.CreateDirectory(inside);
        Process(dir.Path, 100, "/usr/bin/wine");
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "100", "cwd"), inside);

        Assert.True(new WineProcesses(dir.Path).IsPrefixLive(prefix));
    }

    /// <summary>
    /// The fd source, which catches a process holding a prefix file open — a wineserver that has
    /// the registry open, for instance — without mapping it or living in it.
    /// </summary>
    [LinuxFact]
    public void A_process_holding_a_prefix_file_open_makes_it_live()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var prefix = Path.Combine(dir.Path, "prefix");
        Directory.CreateDirectory(prefix);
        var file = Path.Combine(prefix, "user.reg");
        File.WriteAllText(file, "#arch=win64\n");
        Process(dir.Path, 100, "/usr/bin/wineserver");
        Directory.CreateDirectory(Path.Combine(dir.Path, "100", "fd"));
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "100", "fd", "3"), file);

        Assert.True(new WineProcesses(dir.Path).IsPrefixLive(prefix));
    }
}
