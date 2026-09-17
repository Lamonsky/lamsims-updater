using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class EaClientRemoveTests
{
    private static Task<UnlockerResult> RemoveAsync(InstallFixture f, UnlockerTarget target)
    {
        f.Reports.Clear();
        return f.Backend.RemoveAsync(target, new SyncProgress<UnlockerProgress>(f.Reports.Add),
                                    CancellationToken.None);
    }

    [Fact]
    public async Task Removing_what_is_not_installed_succeeds_without_touching_anything()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        var before = f.Snapshot();

        var result = await RemoveAsync(f, target);

        Assert.True(result.Success, result.Error);
        Assert.Equal(before, f.Snapshot());
        Assert.DoesNotContain(f.Host.Calls, c => c.StartsWith("Kill:", StringComparison.Ordinal));
    }

    // Step 1 probes before step 2 checks elevation, so a machine with nothing installed is not
    // asked for administrator rights. IsElevated is cleared because both orders succeed with it set.
    [Fact]
    public async Task Nothing_installed_is_reported_without_demanding_elevation()
    {
        using var f = new InstallFixture();
        f.Host.IsElevated = false;
        var target = await f.TargetAsync();

        var result = await RemoveAsync(f, target);

        // Success alone would pass against an implementation that checked elevation first, so the
        // flag is asserted with it.
        Assert.True(result.Success, result.Error);
        Assert.False(result.RequiresElevation);
    }

    [Fact]
    public async Task Without_elevation_an_installed_unlocker_is_left_in_place()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        f.Host.IsElevated = false;

        var before = f.Snapshot();

        var result = await RemoveAsync(f, target);

        // The snapshot is checked too: a surviving DLL alone would pass against a removal that
        // deleted the config directory before checking rights.
        Assert.True(result.RequiresElevation);
        Assert.True(File.Exists(Path.Combine(target.ClientPath, "version.dll")));
        Assert.Equal(before, f.Snapshot());
    }

    // The port never creates this task, but it must delete one an upstream install left behind.
    [Fact]
    public async Task The_scheduled_task_upstream_created_is_deleted_on_an_ea_app_removal()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        await f.InstallAsync(target);

        Assert.True((await RemoveAsync(f, target)).Success);

        Assert.Contains("DeleteTask:copy_dlc_unlocker", f.Host.Calls);
    }

    [Fact]
    public async Task An_origin_removal_does_not_touch_the_scheduler_or_machine_ini()
    {
        using var f = new InstallFixture(ClientKind.Origin);
        var target = await f.TargetAsync();
        await f.InstallAsync(target);

        // An Origin machine may still carry machine.ini from an earlier EA app install.
        Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.MachineIniFile)!);
        await File.WriteAllTextAsync(f.Paths.MachineIniFile,
                                     "[machine]\r\nmachine.bgsstandaloneenabled=0\r\n");
        var before = await File.ReadAllBytesAsync(f.Paths.MachineIniFile);

        Assert.True((await RemoveAsync(f, target)).Success);

        Assert.DoesNotContain(f.Host.Calls, c => c.StartsWith("DeleteTask", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Paths.MachineIniFile));
    }

    [Fact]
    public async Task The_staged_copy_and_its_empty_directories_are_removed()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        await f.InstallAsync(target);
        var parent = Directory.GetParent(target.ClientPath)!.FullName;

        Assert.True((await RemoveAsync(f, target)).Success);

        Assert.False(Directory.Exists(Path.Combine(parent, "StagedEADesktop")));
    }

    [Fact]
    public async Task The_autostart_value_is_put_back_and_the_backup_deleted()
    {
        using var f = new InstallFixture();
        const string original = @"C:\EA\EADesktop.exe -silent";
        f.Host.Autostart("EADM", original);
        var target = await f.TargetAsync();
        await f.InstallAsync(target);
        Assert.DoesNotContain("EADM", f.Host.AutostartValues.Keys);

        Assert.True((await RemoveAsync(f, target)).Success);

        Assert.Equal(original, f.Host.AutostartValues["EADM"].Value);
        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));
    }

    // Restoring the autostart value hits the same policy-restricted key, and step 8 is non-fatal.
    // If SecurityException escapes, removal reports failure after removing everything.
    [Fact]
    public async Task A_registry_write_the_policy_forbids_warns_instead_of_failing_the_removal()
    {
        using var f = new InstallFixture();
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        f.Host.ThrowSecurityOnAutostartWrite = true;

        var result = await RemoveAsync(f, target);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("autostart"));
        Assert.False(File.Exists(Path.Combine(target.ClientPath, "version.dll")));
    }

    /// <summary>
    /// An EA app self-update can delete version.dll on its own, which is why the StagedEADesktop
    /// copy exists. Step 1 must not read that as "nothing to remove": the install also flipped the
    /// autostart value and left a backup, and a missing DLL undoes neither.
    /// </summary>
    [Fact]
    public async Task A_dll_that_disappeared_on_its_own_still_gets_autostart_restored()
    {
        using var f = new InstallFixture();
        const string original = @"C:\EA\EADesktop.exe -silent";
        f.Host.Autostart("EADM", original);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        File.Delete(Path.Combine(target.ClientPath, "version.dll"));

        var result = await RemoveAsync(f, target);

        Assert.True(result.Success, result.Error);
        Assert.Equal(original, f.Host.AutostartValues["EADM"].Value);
        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));
    }

    [Fact]
    public async Task A_removal_with_no_backup_recorded_is_a_no_op_success()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        await f.InstallAsync(target);

        var result = await RemoveAsync(f, target);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(f.Host.Calls, c => c.StartsWith("WriteAutostart", StringComparison.Ordinal));
    }

    // Matching roots before and after would pass against an install that did nothing, hence the
    // mid-point assertion.
    [Fact]
    public async Task Install_then_remove_returns_every_guarded_root_to_its_prior_state()
    {
        using var f = new InstallFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.MachineIniFile)!);
        await File.WriteAllBytesAsync(f.Paths.MachineIniFile,
            System.Text.Encoding.UTF8.GetBytes("[machine]\r\nfoo=1\r\n"));
        var machineIni = await File.ReadAllBytesAsync(f.Paths.MachineIniFile);
        var target = await f.TargetAsync();
        var before = f.Snapshot();

        Assert.True((await f.InstallAsync(target)).Success);

        // The mid-point: the install really did something.
        Assert.NotEqual(before, f.Snapshot());
        Assert.True(File.Exists(Path.Combine(target.ClientPath, "version.dll")));

        Assert.True((await RemoveAsync(f, target)).Success);

        Assert.Equal(before, f.Snapshot());
        Assert.Equal(machineIni, await File.ReadAllBytesAsync(f.Paths.MachineIniFile));
    }

    [Theory]
    [InlineData(ClientKind.EaApp, 9)]
    [InlineData(ClientKind.Origin, 6)]
    public async Task Removal_progress_runs_from_zero_to_total(ClientKind kind, int total)
    {
        using var f = new InstallFixture(kind);
        var target = await f.TargetAsync();
        await f.InstallAsync(target);

        Assert.True((await RemoveAsync(f, target)).Success);

        Assert.All(f.Reports, r => Assert.Equal(total, r.Total));
        Assert.Equal(total + 1, f.Reports.Count);
        Assert.Equal(Enumerable.Range(0, total + 1), f.Reports.Select(r => r.Completed));
        Assert.Equal(total, f.Reports[^1].Completed);
    }

    // A backup that is kept makes step 1 find work on every later run, so removal demands
    // elevation and stops the client each time while restoring nothing.
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ not json")]
    [InlineData("{\"Name\":\"EADM\"}")]
    public async Task A_backup_that_cannot_be_used_is_discarded_so_removal_converges(string content)
    {
        using var f = new InstallFixture();
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        await File.WriteAllTextAsync(f.App.UnlockerAutostartBackupFile, content);

        var result = await RemoveAsync(f, target);

        // Deleting the backup is what makes the second removal below a no-op.
        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));

        var second = await RemoveAsync(f, target);
        Assert.True(second.Success, second.Error);
        Assert.False(second.RequiresElevation);
    }

    /// <summary>
    /// The client can re-create its own Run entry between install and removal, with a newer path.
    /// Restoring the recorded value over it would silently downgrade the user's autostart.
    /// </summary>
    [Fact]
    public async Task A_re_created_autostart_entry_is_not_overwritten_by_the_backup()
    {
        using var f = new InstallFixture();
        f.Host.Autostart("EADM", @"C:\EA\old\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        const string newer = @"C:\EA\new\EADesktop.exe -silent";
        f.Host.Autostart("EADM", newer);

        var result = await RemoveAsync(f, target);

        // The backup is cleared even though the value is left alone, so removal converges.
        Assert.Equal(newer, f.Host.AutostartValues["EADM"].Value);
        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));
        Assert.True(result.Success, result.Error);
    }

    // With no autostart value there is no backup, and a self-update can delete version.dll, so
    // this client's own install record has to be enough on its own for step 1 to find work. The
    // shared config directory does not count, because it belongs to every client in the scope.
    [Fact]
    public async Task The_install_record_alone_is_enough_to_remove()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));
        File.Delete(Path.Combine(target.ClientPath, "version.dll"));
        var parent = Directory.GetParent(target.ClientPath)!.FullName;

        Assert.True((await RemoveAsync(f, target)).Success);

        // Either one alone would pass for an early return that happened to leave the other absent.
        Assert.False(Directory.Exists(f.Paths.ConfigDirectory));
        Assert.False(Directory.Exists(Path.Combine(parent, "StagedEADesktop")));
    }

    /// <summary>
    /// StagedEADesktop is EA's folder, not ours, and upstream only deletes it when empty. Something
    /// else may legitimately be staged there mid-update; a recursive delete would take it.
    /// </summary>
    [Fact]
    public async Task Foreign_content_in_StagedEADesktop_survives_removal()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        await f.InstallAsync(target);
        var inner = Path.Combine(Directory.GetParent(target.ClientPath)!.FullName,
                                 "StagedEADesktop", "EA Desktop");
        var foreign = Path.Combine(inner, "EADesktop.exe");
        await File.WriteAllTextAsync(foreign, "not ours");

        Assert.True((await RemoveAsync(f, target)).Success);

        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(Path.Combine(inner, "version.dll")));
    }

    /// <summary>
    /// A value that was never text cannot be put back as text: writing "1" into a slot that held a
    /// REG_DWORD changes what the client reads, so it is warned about and dropped.
    /// </summary>
    [Fact]
    public async Task An_autostart_value_that_is_not_text_is_not_put_back_as_text()
    {
        using var f = new InstallFixture();
        f.Host.Autostart("EADM", "1", AutostartValueKind.Unsupported);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        var result = await RemoveAsync(f, target);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("not a text value"));
        Assert.DoesNotContain("EADM", f.Host.AutostartValues.Keys);
        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));
    }

    [Fact]
    public async Task The_autostart_value_keeps_its_registry_kind_across_the_round_trip()
    {
        using var f = new InstallFixture();
        const string unexpanded = @"%ProgramFiles%\EA\EADesktop.exe";
        f.Host.Autostart("EADM", unexpanded, AutostartValueKind.ExpandString);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        Assert.True((await RemoveAsync(f, target)).Success);

        // Restored as a plain string, the client would read a literal "%ProgramFiles%" it no
        // longer expands, so the kind is asserted too.
        var restored = f.Host.AutostartValues["EADM"];
        Assert.Equal(unexpanded, restored.Value);
        Assert.Equal(AutostartValueKind.ExpandString, restored.Kind);
    }
}
