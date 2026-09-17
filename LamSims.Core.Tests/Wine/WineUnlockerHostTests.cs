using System;
using System.IO;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Logging;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class WineHostTests
{
    private const string EaClientPath =
        @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe";

    private static void RegisterEa(PrefixFixture f, string windowsPath = EaClientPath,
                                   string valueForm = "\"{0}\"")
    {
        f.AddClient(ClientKind.EaApp, windowsPath);
        var escaped = windowsPath.Replace("\\", "\\\\");
        f.WriteSystemReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                         + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                         + $"\"ClientPath\"={string.Format(valueForm, escaped)}\n");
    }

    [LinuxFact]
    public void An_opened_prefix_is_available_and_counts_as_elevated()
    {
        using var f = new PrefixFixture();
        var host = new WineUnlockerHost(f.Open());

        // Elevated because prefix contents are user-owned. A false here makes the engine's step 1
        // return NeedsElevation and the UI offer a relaunch that cannot help.
        Assert.True(host.IsAvailable);
        Assert.True(host.IsElevated);
        Assert.False(host.TryRelaunchElevated());
    }

    /// <summary>
    /// The resolved Linux path, asserted through what the engine then does with it. A host returning
    /// the raw value makes detection find nothing on every prefix with no error, because
    /// Path.GetDirectoryName of a backslash path is an empty string on Linux.
    /// </summary>
    [LinuxFact]
    public void ReadClientPath_returns_a_path_the_engine_can_split()
    {
        using var f = new PrefixFixture();
        RegisterEa(f);
        var host = new WineUnlockerHost(f.Open());

        var value = host.ReadClientPath(ClientRegistryKey.EaDesktop);

        Assert.NotNull(value);
        Assert.True(File.Exists(value));
        Assert.True(Directory.Exists(Path.GetDirectoryName(value)));
    }

    /// <summary>
    /// A ClientPath stored as REG_EXPAND_SZ is 125-occurrences-common in a real registry, and its
    /// %VAR% has to be expanded before resolution or the path never exists.
    /// </summary>
    [LinuxFact]
    public void An_expand_string_client_path_is_expanded_then_resolved()
    {
        using var f = new PrefixFixture();
        f.AddClient(ClientKind.EaApp, EaClientPath);
        f.WriteSystemReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                         + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                         + "\"ClientPath\"=str(2):\"%ProgramFiles%\\\\Electronic Arts\\\\EA Desktop"
                         + "\\\\EA Desktop\\\\EADesktop.exe\"\n");

        Assert.True(File.Exists(new WineUnlockerHost(f.Open())
            .ReadClientPath(ClientRegistryKey.EaDesktop)));
    }

    [LinuxFact]
    public void A_key_that_is_not_there_reads_as_null_without_throwing()
    {
        using var f = new PrefixFixture();
        var host = new WineUnlockerHost(f.Open());

        Assert.Null(host.ReadClientPath(ClientRegistryKey.Origin));
        Assert.Null(host.ReadClientPath(ClientRegistryKey.EaDesktopWow6432));
    }

    /// <summary>
    /// A registered client whose directory is gone: the value is there, the path is not. Reported
    /// with the exact string, because "no clients found" and "your EA app moved" are different
    /// facts about the prefix. Reported to the LOG: it is a fact about an environment nobody
    /// claimed was usable, and the window's note list is for what a user must act on.
    /// </summary>
    [LinuxFact]
    public void A_registered_client_whose_path_is_missing_is_reported()
    {
        using var f = new PrefixFixture();
        f.WriteSystemReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                         + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                         + "\"ClientPath\"=\"D:\\\\Gone\\\\EADesktop.exe\"\n");
        var log = new RecordingLogSink();
        var host = new WineUnlockerHost(f.Open(), log);

        Assert.Null(host.ReadClientPath(ClientRegistryKey.EaDesktop));
        Assert.True(host.SawClientValue);
        Assert.True(log.Logged("does not exist in this prefix", LogSeverity.Warning),
                    string.Join("\n", log.Texts));
    }

    [LinuxFact]
    public void SawClientValue_is_false_when_no_key_holds_a_value()
    {
        using var f = new PrefixFixture();
        var host = new WineUnlockerHost(f.Open());

        host.ReadClientPath(ClientRegistryKey.EaDesktop);
        host.ReadClientPath(ClientRegistryKey.Origin);

        Assert.False(host.SawClientValue);
    }

    /// <summary>
    /// The load-bearing one. Wiring this to IWineProcesses is the obvious move and it breaks every
    /// install with the client running: the engine's gate goes non-empty, kills nothing, sees
    /// survivors and returns a hard failure. The pair of assertions is the point — "returns empty"
    /// alone would also hold for a host that reported empty because it looked in the wrong place.
    /// </summary>
    [LinuxFact]
    public void RunningClientProcesses_is_always_empty_and_nothing_is_ever_killed()
    {
        using var f = new PrefixFixture();
        var host = new WineUnlockerHost(f.Open());

        Assert.Empty(host.RunningClientProcesses(["EADesktop.exe"]));
        Assert.Empty(host.KillClientProcesses(["EADesktop.exe"], TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// The Run key is inert under Wine and the scheduled task does not exist there. Each must be a
    /// silent no-op: a throw would fail the engine's non-fatal autostart step after version.dll is
    /// already on disk.
    /// </summary>
    [LinuxFact]
    public void The_autostart_and_scheduled_task_members_are_silent_no_ops()
    {
        using var f = new PrefixFixture();
        var host = new WineUnlockerHost(f.Open());

        Assert.Null(host.ReadAutostartValue("EADM"));
        host.WriteAutostartValue("EADM", new AutostartValue("x", AutostartValueKind.String));
        host.RemoveAutostartValue("EADM");
        host.DeleteScheduledTask("copy_dlc_unlocker");

        // Still null: a write that actually stored something would make removal restore a value
        // into a registry the client never reads.
        Assert.Null(host.ReadAutostartValue("EADM"));
    }

    /// <summary>
    /// HKLM lives in system.reg and HKCU in user.reg, and a client can be registered in either. A
    /// host reading only one file misses every prefix that used the other.
    /// </summary>
    [LinuxFact]
    public void A_client_registered_in_user_reg_is_found_too()
    {
        using var f = new PrefixFixture();
        var directory = f.AddClient(ClientKind.Origin, @"C:\Program Files\Origin\Origin.exe");
        f.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                       + "[Software\\\\Origin] 0\n"
                       + "\"ClientPath\"=\"C:\\\\Program Files\\\\Origin\\\\Origin.exe\"\n");

        var value = new WineUnlockerHost(f.Open()).ReadClientPath(ClientRegistryKey.Origin);

        Assert.Equal(PathIdentity.Canonical(directory),
                     PathIdentity.Canonical(Path.GetDirectoryName(value)));
    }

    [LinuxFact]
    public void The_wow6432_keys_map_to_wines_own_spelling()
    {
        using var f = new PrefixFixture();
        f.AddClient(ClientKind.Origin, @"C:\Program Files (x86)\Origin\Origin.exe");
        f.WriteSystemReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                         + "[Software\\\\Wow6432Node\\\\Origin] 0\n"
                         + "\"ClientPath\"=\"C:\\\\Program Files (x86)\\\\Origin\\\\Origin.exe\"\n");

        Assert.NotNull(new WineUnlockerHost(f.Open())
            .ReadClientPath(ClientRegistryKey.OriginWow6432));
    }

    /// <summary>
    /// system.reg and user.reg are checked in order, but the first file with a stale path should
    /// not prevent checking the next file. A prefix with HKLM stale and HKCU valid — an
    /// install-then-move, or a per-user registration after a machine-wide one — must find the
    /// working path in the second hive, not report "does not exist".
    /// </summary>
    [LinuxFact]
    public void Both_hives_are_checked_even_if_the_first_has_a_stale_path()
    {
        using var f = new PrefixFixture();
        var directory = f.AddClient(ClientKind.EaApp, EaClientPath);
        // system.reg has a stale path
        f.WriteSystemReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                         + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                         + "\"ClientPath\"=\"D:\\\\Gone\\\\EADesktop.exe\"\n");
        // user.reg has the valid path
        f.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                       + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                       + $"\"ClientPath\"=\"C:\\\\Program Files\\\\Electronic Arts\\\\EA Desktop"
                       + "\\\\EA Desktop\\\\EADesktop.exe\"\n");

        var value = new WineUnlockerHost(f.Open()).ReadClientPath(ClientRegistryKey.EaDesktop);

        Assert.Equal(PathIdentity.Canonical(directory),
                     PathIdentity.Canonical(Path.GetDirectoryName(value)));
    }

    /// <summary>
    /// A REG_EXPAND_SZ ClientPath under a non-primary user must expand with THAT user's directory,
    /// not the alphabetically first one, or a client installed under the second user resolves to a
    /// path that does not exist and reads as absent.
    ///
    /// %LOCALAPPDATA%, deliberately, and not %ProgramFiles%: only the user-dependent variables
    /// expand differently per user, so with %ProgramFiles% every candidate is the same string and
    /// the test held for an implementation that expanded against the primary user alone.
    ///
    /// Opened as a user that is NOT one of the directories, which is what makes UserDirectories
    /// return more than one candidate: asked for a name that matches, it returns that single
    /// directory and the per-user loop has nothing to iterate. The client is placed only under
    /// "beta", so an expansion that tries just the first candidate resolves to a file that is not
    /// there.
    /// </summary>
    [LinuxFact]
    public void An_expand_string_path_under_a_second_user_expands_correctly()
    {
        // "alpha" sorts before "beta", so a first-candidate-only expansion picks alpha and misses.
        using var f = new PrefixFixture();
        f.AddUser("alpha");
        f.AddUser("beta");

        // Under beta's own AppData, which is what %LOCALAPPDATA% has to resolve to.
        var client = f.AddClient(
            ClientKind.EaApp,
            @"C:\users\beta\AppData\Local\Electronic Arts\EA Desktop\EADesktop.exe");

        f.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                       + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                       + "\"ClientPath\"=str(2):\"%LOCALAPPDATA%\\\\Electronic Arts"
                       + "\\\\EA Desktop\\\\EADesktop.exe\"\n");

        var value = new WineUnlockerHost(f.Open(null, "nobody")).ReadClientPath(ClientRegistryKey.EaDesktop);

        Assert.NotNull(value);
        Assert.True(File.Exists(value), $"'{value}' does not exist");

        // Names beta's directory, not the default user's: File.Exists alone would also hold for an
        // implementation that happened to find a file under another user.
        Assert.Equal(Path.Combine(client, "EADesktop.exe"), value);
    }

    /// <summary>
    /// When both hives hold stale values, the diagnostic must fire exactly once, not twice.
    /// Combining reports produces noise and makes the user experience confusing.
    /// </summary>
    [LinuxFact]
    public void A_stale_path_in_both_hives_reports_once()
    {
        using var f = new PrefixFixture();
        var log = new RecordingLogSink();
        var staleWindows = @"D:\Gone\EADesktop.exe";

        // Both hives have the same stale path
        f.WriteSystemReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                         + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                         + $"\"ClientPath\"=\"{staleWindows.Replace(@"\", @"\\")}\"\n");
        f.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                       + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
                       + $"\"ClientPath\"=\"{staleWindows.Replace(@"\", @"\\")}\"\n");

        var host = new WineUnlockerHost(f.Open(), log);
        var value = host.ReadClientPath(ClientRegistryKey.EaDesktop);

        Assert.Null(value);
        Assert.True(host.SawClientValue);
        Assert.Single(log.Lines);
        Assert.True(log.Logged("does not exist in this prefix"));
    }
}
