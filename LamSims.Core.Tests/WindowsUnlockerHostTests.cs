using System;
using Xunit;
using System.Runtime.Versioning;
using LamSims.Core.Unlocking;
using Microsoft.Win32;

namespace LamSims.Core.Tests;

/// <summary>
/// Exercises WindowsUnlockerHost on the one platform where it can run. Everything here returns
/// early off Windows, so on Linux this file proves compilation and nothing more, which is why the
/// windows-latest CI leg exists. Registry work is confined to HKCU under GUID-suffixed value names
/// and removed in finally: the runner is an administrator, so an HKLM write would do real damage.
///
/// There is no test for Process.Kill(entireProcessTree: true) raising AggregateException:
/// WindowsUnlockerHost.cs:150 calls process.Kill(), so that path does not exist.
/// </summary>
public class WindowsUnlockerHostTests
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string TestValueName() => "lamsims-test-" + Guid.NewGuid().ToString("N");

    [SupportedOSPlatform("windows")]
    private static void RemoveRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteRunValue(string name, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(name, value, kind);
    }

    [Fact]
    public void A_string_autostart_value_round_trips()
    {
        if (!OperatingSystem.IsWindows()) return;

        var host = new WindowsUnlockerHost();
        var name = TestValueName();
        try
        {
            host.WriteAutostartValue(name, new AutostartValue(@"C:\EA\client.exe", AutostartValueKind.String));

            var read = host.ReadAutostartValue(name);

            Assert.NotNull(read);
            Assert.Equal(@"C:\EA\client.exe", read.Value);
            Assert.Equal(AutostartValueKind.String, read.Kind);
        }
        finally { RemoveRunValue(name); }
    }

    /// <summary>
    /// Correct by reading alone today. A
    /// REG_EXPAND_SZ read expanded and written back as plain text rewrites the user's
    /// "%ProgramFiles%\..." into one machine's answer to it, so both the value and its kind have
    /// to survive. Asserting the pair is the point: the value alone round-trips even when the
    /// kind is dropped.
    /// </summary>
    [Fact]
    public void An_expand_string_autostart_value_keeps_its_kind()
    {
        if (!OperatingSystem.IsWindows()) return;

        var host = new WindowsUnlockerHost();
        var name = TestValueName();
        try
        {
            host.WriteAutostartValue(name,
                new AutostartValue(@"%ProgramFiles%\EA\client.exe", AutostartValueKind.ExpandString));

            var read = host.ReadAutostartValue(name);

            Assert.NotNull(read);
            Assert.Equal(@"%ProgramFiles%\EA\client.exe", read.Value);
            Assert.Equal(AutostartValueKind.ExpandString, read.Kind);
        }
        finally { RemoveRunValue(name); }
    }

    /// <summary>
    /// Upstream deletes the autostart entry type-blind, so a REG_DWORD entry must be reported
    /// (as Unsupported, since it cannot be written back as text) and must still be removable.
    /// Reporting null would leave the client starting at login.
    /// </summary>
    [Fact]
    public void A_non_text_autostart_value_reads_as_unsupported_and_is_still_removed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var host = new WindowsUnlockerHost();
        var name = TestValueName();
        try
        {
            WriteRunValue(name, 1, RegistryValueKind.DWord);

            var read = host.ReadAutostartValue(name);
            Assert.NotNull(read);
            Assert.Equal(AutostartValueKind.Unsupported, read.Kind);

            host.RemoveAutostartValue(name);

            Assert.Null(host.ReadAutostartValue(name));
        }
        finally { RemoveRunValue(name); }
    }

    [Fact]
    public void An_absent_autostart_value_reads_as_null()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(new WindowsUnlockerHost().ReadAutostartValue(TestValueName()));
    }

    /// <summary>
    /// /Delete's exit code cannot distinguish "there was no task" (the normal case, since this
    /// port never creates one) from "you may not touch it", and its messages are localised, so
    /// DeleteScheduledTask queries first. Both halves are asserted: an absent task is a silent
    /// no-op, and a task that does exist is actually gone afterwards.
    /// </summary>
    private static int QueryExitCode(string task)
    {
        using var query = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "schtasks.exe", $"/Query /TN {task}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        });
        if (query is null) return -1;
        query.WaitForExit();
        return query.ExitCode;
    }

    [Fact]
    public void Deleting_an_absent_scheduled_task_is_a_no_op()
    {
        if (!OperatingSystem.IsWindows()) return;

        var task = "LamSims-absent-" + Guid.NewGuid().ToString("N");

        // Asserted before as well as after: without the first check this test would pass even if a
        // task by that name happened to exist, which is the one premise it depends on.
        Assert.NotEqual(0, QueryExitCode(task));

        new WindowsUnlockerHost().DeleteScheduledTask(task);

        Assert.NotEqual(0, QueryExitCode(task));
    }

    [Fact]
    public void Deleting_an_existing_scheduled_task_removes_it()
    {
        if (!OperatingSystem.IsWindows()) return;

        var task = "LamSims-test-" + Guid.NewGuid().ToString("N");
        try
        {
            var created = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "schtasks.exe",
                $"/Create /TN {task} /TR cmd.exe /SC ONCE /ST 23:59 /F")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            Assert.NotNull(created);
            created.WaitForExit();

            // schtasks /Create in the root folder needs administrator. The CI runner is elevated, so
            // this exercises the real path there; on an unelevated developer machine the test declines
            // rather than reporting a failure caused by the shell it was started from.
            if (created.ExitCode != 0) return;

            new WindowsUnlockerHost().DeleteScheduledTask(task);

            Assert.NotEqual(0, QueryExitCode(task));
        }
        finally
        {
            // Best effort: the assertions above may have failed with the task still registered, and
            // this file must not leave OS state behind on a machine that is not disposable.
            using var cleanup = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "schtasks.exe", $"/Delete /TN {task} /F")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            cleanup?.WaitForExit();
        }
    }

    /// <summary>
    /// The smoke payload: on a runner with no EA app and no Origin,
    /// the three registry reads and the elevation check must return rather than throw, and
    /// detection must report nothing rather than a phantom target.
    /// </summary>
    [Fact]
    public void Detection_on_a_machine_without_a_client_returns_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var host = new WindowsUnlockerHost();

        Assert.True(host.IsAvailable);
        _ = host.IsElevated;

        foreach (var key in Enum.GetValues<ClientRegistryKey>())
            _ = host.ReadClientPath(key);

        Assert.Empty(host.RunningClientProcesses(["NoSuchClient.exe"]));
    }
}
