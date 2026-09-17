using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Runtime.Versioning;
using LamSims.Core.Logging;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public sealed class InstallFixture : IDisposable
{
    public TempDir Dir { get; } = new();
    public FakeUnlockerHost Host { get; } = new();
    public FakeDelayProvider Delays { get; } = new();
    public List<UnlockerProgress> Reports { get; } = [];
    public UnlockerPaths Paths { get; }
    public AppPaths App { get; }
    public EaClientUnlockerBackend Backend { get; }
    public UnlockerInstallRecordStore Records { get; }
    public IUnlockerAssetSource Assets { get; }
    public string DllSource { get; }

    /// <summary>Convenience overload for a test that only cares about the log, at the default client.</summary>
    public InstallFixture(ILogSink log) : this(ClientKind.EaApp, log)
    {
    }

    public InstallFixture(ClientKind kind = ClientKind.EaApp, ILogSink? log = null)
    {
        Paths = new UnlockerPaths(Path.Combine(Dir.Path, "roaming"), Path.Combine(Dir.Path, "common"));

        // Removal deletes the unlocker's own config directory but leaves the "anadius" folder above
        // it, which may hold other anadius tools. Pre-created so a round trip starts from that state.
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConfigDirectory)!);
        App = new AppPaths(Path.Combine(Dir.Path, "app"));
        App.EnsureCreated();

        // The client lives two levels down so the StagedEADesktop sibling has somewhere to go.
        var clientDir = Path.Combine(Dir.Path, "Program Files", "EA Desktop", "EA Desktop");
        Directory.CreateDirectory(clientDir);
        Host.ClientPaths[kind == ClientKind.EaApp
            ? ClientRegistryKey.EaDesktop
            : ClientRegistryKey.Origin] = Path.Combine(clientDir, "EADesktop.exe");

        DllSource = Path.Combine(Dir.Path, "cache", "ea_app_version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(DllSource)!);
        File.WriteAllBytes(DllSource, [0x4D, 0x5A, 0x90, 0x00]);
        Assets = new StubAssetSource(DllSource);

        // The store is constructed here and injected rather than left to the backend's own
        // fallback, so the optional-store parameter has a live caller and its non-null branch is
        // the one every test below runs through.
        Records = new UnlockerInstallRecordStore(App);
        Backend = new EaClientUnlockerBackend(Host, Paths, App, Delays, Records, log);
    }

    public Task<UnlockerTarget> TargetAsync() =>
        Backend.DetectTargetsAsync(CancellationToken.None).ContinueWith(t => t.Result[0]);

    public Task<UnlockerResult> InstallAsync(UnlockerTarget target) =>
        Backend.InstallAsync(target, Assets, new SyncProgress<UnlockerProgress>(Reports.Add),
                             CancellationToken.None);

    /// <summary>
    /// Registers Origin beside the first client, in the same scope, so a test can exercise two
    /// clients sharing one configuration directory. Returns its directory.
    /// </summary>
    public string AddOrigin()
    {
        var directory = Path.Combine(Dir.Path, "Program Files", "Origin");
        Directory.CreateDirectory(directory);
        Host.ClientPaths[ClientRegistryKey.Origin] = Path.Combine(directory, "Origin.exe");
        return directory;
    }

    public Task<UnlockerResult> RemoveAsync(UnlockerTarget target) =>
        Backend.RemoveAsync(target, new SyncProgress<UnlockerProgress>(Reports.Add),
                            CancellationToken.None);

    /// <summary>The roots an aborted install must leave unchanged. DownloadPaths is excluded: a
    /// verified DLL may legitimately have been cached before the step under test refused.</summary>
    public string[] GuardedRoots =>
    [
        Paths.Roaming,
        Paths.CommonAppData,
        Path.Combine(Dir.Path, "Program Files"),
    ];

    public string Snapshot() => string.Join("\n", GuardedRoots
        .Where(Directory.Exists)
        .SelectMany(r => Directory.GetFileSystemEntries(r, "*", SearchOption.AllDirectories))
        .Order());

    public void Dispose() => Dir.Dispose();
}

public sealed class StubAssetSource(string path) : IUnlockerAssetSource
{
    public int Calls { get; private set; }

    /// <summary>Set to make the fetch fail the way a refused or unreachable cache does.</summary>
    public Exception? Throw { get; set; }

    /// <summary>The verified payload, when a test needs it to differ from the file on disk.</summary>
    public byte[]? Bytes { get; set; }

    /// <summary>
    /// Runs when the bytes are handed over, which is where the real window between the liveness
    /// check and the override write is: the fetch takes seconds.
    /// </summary>
    public Action? OnRead { get; set; }

    public Task<ReadOnlyMemory<byte>> GetDllAsync(ClientKind client, CancellationToken ct)
    {
        Calls++;
        if (Throw is not null) throw Throw;
        OnRead?.Invoke();
        return Task.FromResult<ReadOnlyMemory<byte>>(Bytes ?? File.ReadAllBytes(path));
    }
}

public class EaClientInstallTests
{
    // The asset-source call count is asserted too: the result flag alone would pass for an install
    // that half-ran before refusing.
    [Fact]
    public async Task Without_elevation_nothing_is_touched_and_no_asset_is_fetched()
    {
        using var f = new InstallFixture();
        f.Host.IsElevated = false;
        var target = await f.TargetAsync();
        var before = f.Snapshot();

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.True(result.RequiresElevation);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(0, ((StubAssetSource)f.Assets).Calls);
    }

    // The config directory is step 4, the first mutation the abort has to prevent. The download
    // directory is outside GuardedRoots because step 2 may already have cached the DLL.
    [Fact]
    public async Task A_client_that_outlives_the_kill_timeout_aborts_before_anything_is_written()
    {
        using var f = new InstallFixture();

        // Both lists are needed: the step only tries to kill when RunningClientProcesses reports
        // something, so seeding Survivors alone leaves the kill unreached and the install succeeds.
        f.Host.Running.Add("EADesktop");
        f.Host.Survivors.Add("EADesktop");
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.Contains("EADesktop", result.Error);
        Assert.False(Directory.Exists(f.Paths.ConfigDirectory));
    }

    [Fact]
    public async Task The_post_kill_settle_is_awaited_through_the_delay_provider_not_slept()
    {
        using var f = new InstallFixture();
        f.Host.Running.Add("EADesktop");
        var target = await f.TargetAsync();

        await f.InstallAsync(target);

        Assert.NotEmpty(f.Delays.Delays);
    }

    // A group-policy-restricted Run key throws SecurityException, which the host does not catch.
    // The backend's filter has to, or the non-fatal autostart step aborts the install after the
    // DLL is already in place.
    [Fact]
    public async Task A_registry_write_the_policy_forbids_warns_instead_of_aborting_the_install()
    {
        using var f = new InstallFixture();
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        f.Host.ThrowSecurityOnAutostartWrite = true;
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        // Success alone would pass against a step that swallowed the failure; the warning and the
        // DLL are both checked.
        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("autostart"));
        Assert.True(File.Exists(Path.Combine(target.ClientPath, "version.dll")));
    }

    /// <summary>
    /// Upstream selected processes with a case-insensitive ProcessName.StartsWith("EA"), which kills
    /// unrelated software such as EarTrumpet. This covers the set of names the backend asks about;
    /// the equality matching itself lives in WindowsUnlockerHost, which no test executes.
    /// </summary>
    [Theory]
    [InlineData(ClientKind.EaApp)]
    [InlineData(ClientKind.Origin)]
    public async Task The_client_is_matched_by_exact_process_name_never_by_prefix(ClientKind kind)
    {
        using var f = new InstallFixture(kind);
        f.Host.Running.Add("whatever");
        var target = await f.TargetAsync();

        await f.InstallAsync(target);

        var expected = EaClientUnlockerBackend.ProcessNames[kind];
        Assert.NotEmpty(f.Host.Asked);
        Assert.Equal(expected, f.Host.Asked[0]);

        // "Origin" belongs, being a real process name; a bare "EA" would mean prefix matching is back.
        Assert.DoesNotContain("EA", expected);
        Assert.DoesNotContain(expected, n => n.Contains("lamsims", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_successful_install_writes_both_config_files()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(f.Paths.ConfigFile));
        Assert.True(File.Exists(f.Paths.DlcListFile));
    }

    [Theory]
    [InlineData(ClientKind.EaApp, 10)]
    [InlineData(ClientKind.Origin, 8)]
    public async Task Every_progress_report_carries_the_right_total(ClientKind kind, int total)
    {
        using var f = new InstallFixture(kind);
        var target = await f.TargetAsync();

        await f.InstallAsync(target);

        Assert.NotEmpty(f.Reports);
        Assert.All(f.Reports, r => Assert.Equal(total, r.Total));
        // Range over TOTAL, not over the reports' own count: built from f.Reports.Count this
        // compared a sequence with itself, so an install that emitted nine of its eleven steps
        // passed. Total is a constant of the run, so it is the only side that can disagree.
        Assert.Equal(Enumerable.Range(0, total + 1), f.Reports.Select(r => r.Completed));
    }

    // Upstream deleted its source DLL before staging it, so the staged copy never happened. The
    // cache entry is checked alongside both copies.
    [Fact]
    public async Task An_ea_app_install_places_the_dll_in_both_locations_and_keeps_the_cache()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        var expected = await File.ReadAllBytesAsync(f.DllSource);

        var result = await f.InstallAsync(target);
        Assert.True(result.Success, result.Error);

        var installed = Path.Combine(target.ClientPath, "version.dll");
        var staged = Path.Combine(Directory.GetParent(target.ClientPath)!.FullName,
                                  "StagedEADesktop", "EA Desktop", "version.dll");

        Assert.Equal(expected, await File.ReadAllBytesAsync(installed));
        Assert.Equal(expected, await File.ReadAllBytesAsync(staged));
        Assert.True(File.Exists(f.DllSource), "the cache entry was deleted");
    }

    [Fact]
    public async Task An_origin_install_does_not_create_a_staged_folder()
    {
        using var f = new InstallFixture(ClientKind.Origin);
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.False(Directory.Exists(Path.Combine(
            Directory.GetParent(target.ClientPath)!.FullName, "StagedEADesktop")));
    }

    [Fact]
    public async Task Old_unlocker_files_are_removed_from_the_client_directory()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        foreach (var name in new[] { "version_o.dll", "winhttp.dll", "winhttp_o.dll", "w_stale.ini" })
            await File.WriteAllTextAsync(Path.Combine(target.ClientPath, name), "old");

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.Empty(Directory.GetFiles(target.ClientPath, "w_*.ini"));
        Assert.False(File.Exists(Path.Combine(target.ClientPath, "version_o.dll")));
        Assert.False(File.Exists(Path.Combine(target.ClientPath, "winhttp.dll")));
        Assert.False(File.Exists(Path.Combine(target.ClientPath, "winhttp_o.dll")));
    }

    [Fact]
    public async Task An_existing_machine_ini_gains_the_flag()
    {
        using var f = new InstallFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.MachineIniFile)!);
        await File.WriteAllTextAsync(f.Paths.MachineIniFile, "[machine]\nfoo=1\n");
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.Contains("machine.bgsstandaloneenabled=0",
                        await File.ReadAllTextAsync(f.Paths.MachineIniFile));
    }

    // A missing machine.ini is the common case where EA Desktop has never run. The warning is
    // asserted as well, since success alone would pass against a step that did nothing.
    [Fact]
    public async Task A_missing_machine_ini_warns_without_failing_the_install()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("machine.ini"));
    }

    // The backup lands under AppPaths, not in the config directory that removal deletes.
    [Fact]
    public async Task The_autostart_value_is_recorded_before_it_is_removed()
    {
        using var f = new InstallFixture();
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe -silent");
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.DoesNotContain("EADM", f.Host.AutostartValues.Keys);
        Assert.True(File.Exists(f.App.UnlockerAutostartBackupFile));
        Assert.Contains("-silent", await File.ReadAllTextAsync(f.App.UnlockerAutostartBackupFile));
    }

    [Fact]
    public async Task No_autostart_value_means_no_backup_file_and_no_failure()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));
    }

    /// <summary>
    /// The count alone passes against an off-by-one that never reaches the end, and the final value
    /// alone passes against a run that skipped steps, so contiguity is checked too.
    /// </summary>
    [Theory]
    [InlineData(ClientKind.EaApp, 10)]
    [InlineData(ClientKind.Origin, 8)]
    public async Task Progress_reports_run_from_zero_to_total(ClientKind kind, int total)
    {
        using var f = new InstallFixture(kind);
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);
        Assert.True(result.Success, result.Error);

        Assert.All(f.Reports, r => Assert.Equal(total, r.Total));
        Assert.Equal(total + 1, f.Reports.Count);
        Assert.Equal(Enumerable.Range(0, total + 1), f.Reports.Select(r => r.Completed));
        Assert.Equal(total, f.Reports[^1].Completed);
    }

    // Upstream's CopyDllFile swallows UnauthorizedAccessException, so InstallUnlocker reports
    // success with no DLL on disk.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_client_directory_that_cannot_be_written_fails_rather_than_reporting_success()
    {
        // SetUnixFileMode is a no-op on Windows, where the directory would stay writable and the
        // copy would succeed.
        if (OperatingSystem.IsWindows()) return;

        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        // ReadOnlyDir creates and locks parent/read-only, leaving the directory you hand it
        // writable, so the destination is locked directly here instead.
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(target.ClientPath);
        File.SetUnixFileMode(target.ClientPath, locked);

        try
        {
            var result = await f.InstallAsync(target);

            Assert.False(result.Success);
            Assert.False(File.Exists(Path.Combine(target.ClientPath, "version.dll")));
        }
        finally
        {
            // Restore before the fixture disposes, or TempDir cannot delete the tree.
            File.SetUnixFileMode(target.ClientPath, unlocked);
        }
    }

    // UnauthorizedAccessException is not an IOException, so step 2's filter names it separately.
    // Otherwise a refused download cache escapes past a caller chain that catches nothing.
    [Fact]
    public async Task A_refused_asset_cache_fails_the_install_instead_of_escaping()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        var before = f.Snapshot();
        ((StubAssetSource)f.Assets).Throw =
            new UnauthorizedAccessException("Access to the path is denied.");

        var result = await f.Backend.InstallAsync(target, f.Assets,
            new SyncProgress<UnlockerProgress>(f.Reports.Add), CancellationToken.None);

        // The snapshot is checked too: the flag alone would pass for a step that failed after
        // writing the DLL.
        Assert.False(result.Success);
        Assert.Contains("denied", result.Error);
        Assert.Equal(before, f.Snapshot());
    }

    // Step 10 records the autostart value before removing it, both halves non-fatal. If the backup
    // cannot be written the value has to stay, or nothing can restore it later.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unwritable_backup_leaves_the_autostart_value_alone()
    {
        // File.SetUnixFileMode, used below to lock the backup directory, does not exist on Windows.
        if (OperatingSystem.IsWindows()) return;

        using var f = new InstallFixture();
        const string original = @"C:\EA\EADesktop.exe";
        f.Host.Autostart("EADM", original);
        var target = await f.TargetAsync();

        var mode = File.GetUnixFileMode(f.App.Root);
        File.SetUnixFileMode(f.App.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(f.App.Root, mode);
        }

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("autostart"));
        Assert.Equal(original, f.Host.AutostartValues["EADM"].Value);
    }

    /// <summary>
    /// The install writes the bytes the asset source verified, not whatever the cache file holds by
    /// the time step 7 runs. The cache sits in a download directory the user can point anywhere, and
    /// these two writes happen seconds later under elevation.
    /// </summary>
    [Fact]
    public async Task The_installed_dll_is_the_payload_the_asset_source_verified()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        var verified = new byte[] { 0x4D, 0x5A, 0x01, 0x02, 0x03 };
        ((StubAssetSource)f.Assets).Bytes = verified;

        Assert.True((await f.InstallAsync(target)).Success);

        // Both copies: the client's own and the one that survives an EA app self-update.
        Assert.Equal(verified,
            await File.ReadAllBytesAsync(Path.Combine(target.ClientPath, "version.dll")));
        Assert.Equal(verified, await File.ReadAllBytesAsync(Path.Combine(
            Directory.GetParent(target.ClientPath)!.FullName,
            "StagedEADesktop", "EA Desktop", "version.dll")));
    }

    /// <summary>
    /// Upstream deletes the Run value whatever its type. A backend that only handled text would
    /// leave a REG_DWORD entry in place, so the client keeps starting at login while the install
    /// reports success. Reading a non-string value is WindowsUnlockerHost's half, which no test covers.
    /// </summary>
    [Fact]
    public async Task An_autostart_value_that_is_not_text_is_still_removed()
    {
        using var f = new InstallFixture();
        f.Host.Autostart("EADM", "1", AutostartValueKind.Unsupported);
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        // The backup is checked too, so removal has something to restore from.
        Assert.DoesNotContain("EADM", f.Host.AutostartValues.Keys);
        Assert.True(File.Exists(f.App.UnlockerAutostartBackupFile));
    }
}

public sealed class MultiClientStateTests
{
    // The configuration directory is the unlocker DLL's own, one per scope and shared by both
    // clients, so removal must leave it while any client still holds the DLL. Asserting only that
    // the other client still reports Installed passes while the shared config is destroyed, because
    // that client's DLL is still on disk; the directory assertion is the one that catches it.
    [Fact]
    public async Task Removing_one_client_leaves_the_other_and_the_shared_configuration()
    {
        using var fixture = new InstallFixture();
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);
        Assert.Equal(2, targets.Count);
        foreach (var target in targets)
            Assert.True((await fixture.InstallAsync(target)).Success);

        Assert.True((await fixture.RemoveAsync(targets[1])).Success);

        Assert.True(Directory.Exists(fixture.Paths.ConfigDirectory));
        Assert.Equal(UnlockerState.Installed,
            (await fixture.Backend.GetStatusAsync(targets[0], CancellationToken.None)).State);
    }

    [Fact]
    public async Task Removing_the_last_client_deletes_the_shared_configuration()
    {
        using var fixture = new InstallFixture();
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);
        foreach (var target in targets) await fixture.InstallAsync(target);

        foreach (var target in targets)
            Assert.True((await fixture.RemoveAsync(target)).Success);

        Assert.False(Directory.Exists(fixture.Paths.ConfigDirectory));
    }

    // The step-1 probe keys on this client's own record. A probe that tested the shared config
    // directory or the shared autostart backup would answer "something to remove" for a client with
    // nothing as soon as either client was installed, and the batch UI relies on this probe to make
    // Remove safe on a mixed selection.
    [Fact]
    public async Task Removing_a_client_that_has_nothing_reports_nothing_to_remove()
    {
        using var fixture = new InstallFixture();
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);
        await fixture.InstallAsync(targets[0]);
        fixture.Reports.Clear();

        Assert.True((await fixture.RemoveAsync(targets[1])).Success);

        var steps = fixture.Reports.Select(r => r.Step).ToArray();
        Assert.Contains("Nothing to remove", steps);
        // Elevation was never demanded, because there was nothing to do.
        Assert.DoesNotContain("Checking administrator rights", steps);
    }

    // The autostart value is one machine-wide entry, so exactly one install captures it and only
    // that install's removal restores it. A test that only checks the value is eventually
    // restored passes against a first-remover-wins implementation too.
    [Fact]
    public async Task Only_the_client_that_captured_the_autostart_value_restores_it()
    {
        using var fixture = new InstallFixture();
        fixture.Host.Autostart(EaClientUnlockerBackend.AutostartValueName,
            @"%ProgramFiles%\EA\EADesktop.exe", AutostartValueKind.ExpandString);
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);

        foreach (var target in targets) await fixture.InstallAsync(target);
        Assert.False(fixture.Host.AutostartValues.ContainsKey(
            EaClientUnlockerBackend.AutostartValueName));

        // Origin did not capture it, so removing Origin must not put it back.
        await fixture.RemoveAsync(targets[1]);
        Assert.False(fixture.Host.AutostartValues.ContainsKey(
            EaClientUnlockerBackend.AutostartValueName));

        await fixture.RemoveAsync(targets[0]);
        // TryGetValue, not Assert.Contains: xunit declares the dictionary overload for both
        // IDictionary and IReadOnlyDictionary, and a concrete Dictionary<,> converts to both
        // equally well, so Assert.Contains(key, dict) is CS0121. Every existing assertion in
        // this suite goes through ContainsKey or TryGetValue for the same reason.
        Assert.True(fixture.Host.AutostartValues.TryGetValue(
            EaClientUnlockerBackend.AutostartValueName, out var restored));
        Assert.Equal(@"%ProgramFiles%\EA\EADesktop.exe", restored.Value);
        Assert.Equal(AutostartValueKind.ExpandString, restored.Kind);
    }

    // Two clients and a repair install, which is the only shape that pins the ownership seed and
    // the record's own claim separately. A repair install is allowed — installing over an installed
    // target overwrites the DLL — and finds the value already gone, so an ownership flag
    // recomputed from nothing is false and overwrites the owning record with a disowning one.
    //
    // Removing EA first, while Origin is still recorded, is what makes the record's claim
    // load-bearing: with a sibling on record the "last removal in the scope" disjunct is false, so
    // only OwnsAutostartBackup can carry the restore. The single-client version of this test
    // passed against both a missing seed and a missing claim, because with no sibling that last
    // disjunct is always true.
    [Fact]
    public async Task A_repair_install_keeps_its_autostart_claim_beside_another_client()
    {
        using var fixture = new InstallFixture();
        fixture.Host.Autostart(EaClientUnlockerBackend.AutostartValueName,
            @"C:\EA\EADesktop.exe");
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);

        Assert.True((await fixture.InstallAsync(targets[0])).Success);
        Assert.True((await fixture.InstallAsync(targets[1])).Success);
        Assert.True((await fixture.InstallAsync(targets[0])).Success);

        Assert.True((await fixture.RemoveAsync(targets[0])).Success);

        Assert.True(fixture.Host.AutostartValues.TryGetValue(
            EaClientUnlockerBackend.AutostartValueName, out var restored));
        Assert.Equal(@"C:\EA\EADesktop.exe", restored.Value);
    }

    // The owner has to re-capture on every install. The client can re-create its own Run entry
    // between two installs, and a repair install that kept the first backup deletes the newer
    // value while the backup still describes the older one, so removal puts the stale command line
    // back. The old single-install code refreshed the backup every time, so keeping the first one
    // unconditionally is a regression rather than a new rule.
    [Fact]
    public async Task A_repair_install_re_captures_a_value_the_client_re_created()
    {
        using var fixture = new InstallFixture();
        fixture.Host.Autostart(EaClientUnlockerBackend.AutostartValueName, @"C:\EA\old.exe");
        var target = await fixture.TargetAsync();
        Assert.True((await fixture.InstallAsync(target)).Success);

        // The client put its own entry back, with a newer command line.
        const string newer = @"C:\EA\new.exe -silent";
        fixture.Host.Autostart(EaClientUnlockerBackend.AutostartValueName, newer);
        Assert.True((await fixture.InstallAsync(target)).Success);

        Assert.True((await fixture.RemoveAsync(target)).Success);

        Assert.True(fixture.Host.AutostartValues.TryGetValue(
            EaClientUnlockerBackend.AutostartValueName, out var restored));
        Assert.Equal(newer, restored.Value);
    }

    // Every install made by the current shipped build has no record at all. If a null record
    // read as "not the owner", the upgrade would delete the DLL, tell the user the unlocker was
    // removed, and leave the machine's Run entry deleted for good.
    [Fact]
    public async Task An_install_with_no_record_still_restores_the_autostart_value()
    {
        using var fixture = new InstallFixture();
        fixture.Host.Autostart(EaClientUnlockerBackend.AutostartValueName,
            @"C:\EA\EADesktop.exe");
        var target = await fixture.TargetAsync();
        await fixture.InstallAsync(target);

        // Stand in for a pre-upgrade install: the DLL and the backup exist, the record does not.
        Directory.Delete(fixture.App.UnlockerInstallDirectory, recursive: true);

        Assert.True((await fixture.RemoveAsync(target)).Success);

        Assert.True(fixture.Host.AutostartValues.TryGetValue(
            EaClientUnlockerBackend.AutostartValueName, out var restored));
        Assert.Equal(@"C:\EA\EADesktop.exe", restored.Value);
    }

    // The narrow legacy fallback in the step-1 probe. An EA app self-update can delete
    // version.dll on its own, so a machine installed by a build with no records can have a live
    // autostart backup, no DLL and no record. Without the fallback this reports "nothing to
    // remove" and the Run value stays deleted for good.
    [Fact]
    public async Task A_pre_record_install_whose_dll_vanished_still_restores_the_autostart_value()
    {
        using var fixture = new InstallFixture();
        fixture.Host.Autostart(EaClientUnlockerBackend.AutostartValueName,
            @"C:\EA\EADesktop.exe");
        var target = await fixture.TargetAsync();
        await fixture.InstallAsync(target);

        // A pre-upgrade machine whose client self-updated: no record, no DLL, backup still there.
        Directory.Delete(fixture.App.UnlockerInstallDirectory, recursive: true);
        File.Delete(Path.Combine(target.ClientPath, "version.dll"));

        Assert.True((await fixture.RemoveAsync(target)).Success);

        Assert.True(fixture.Host.AutostartValues.TryGetValue(
            EaClientUnlockerBackend.AutostartValueName, out var restored));
        Assert.Equal(@"C:\EA\EADesktop.exe", restored.Value);
        Assert.False(File.Exists(fixture.App.UnlockerAutostartBackupFile));
    }

    // The records are not the whole answer, and on the existing population they are no answer at
    // all: no install made by the shipped build wrote one, so the store says "nobody else" with
    // complete confidence for every machine that has both clients unlocked today. The other
    // client's DLL on disk is the ground truth the decision has to consult as well.
    [Fact]
    public async Task A_sibling_with_no_record_still_keeps_the_shared_configuration()
    {
        using var fixture = new InstallFixture();
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);
        foreach (var target in targets)
            Assert.True((await fixture.InstallAsync(target)).Success);

        // Stand in for two clients unlocked by a build that wrote no records at all.
        Directory.Delete(fixture.App.UnlockerInstallDirectory, recursive: true);

        Assert.True((await fixture.RemoveAsync(targets[0])).Success);

        Assert.True(Directory.Exists(fixture.Paths.ConfigDirectory));
        Assert.Equal(UnlockerState.Installed,
            (await fixture.Backend.GetStatusAsync(targets[1], CancellationToken.None)).State);
    }

    // Proves the shared-directory decision refuses to guess. Without the Complete check this
    // deletes the configuration the other client reads.
    [Fact]
    public async Task An_unprovable_sibling_leaves_the_shared_configuration_alone()
    {
        using var fixture = new InstallFixture();
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);
        foreach (var t in targets) await fixture.InstallAsync(t);

        var sibling = Directory.GetFiles(fixture.App.UnlockerInstallDirectory)
            .First(f => Path.GetFileName(f).EndsWith("-origin.json", StringComparison.Ordinal));
        await File.WriteAllTextAsync(sibling, "{ not json");

        var result = await fixture.RemoveAsync(targets[0]);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(fixture.Paths.ConfigDirectory));
        Assert.Contains(result.Warnings ?? [], w => w.Contains("could not be determined"));
    }

    // Detection dedups by PATH, not by kind, so two installs of the SAME kind are reachable: the
    // EA app's two registry views can name two different directories that both exist. Every piece
    // of state is keyed by ClientKind, so the record store cannot tell those two apart at all - the
    // second install overwrites the first record, and Others() excludes the caller's own kind by
    // construction, so both record-based guards answer "nobody else". Only a sibling test by path
    // keeps this from deleting the configuration the other directory's version.dll still reads,
    // which is the very defect this work exists to fix.
    [Fact]
    public async Task Removing_one_of_two_ea_app_installs_leaves_the_shared_configuration()
    {
        using var fixture = new InstallFixture();
        var second = Path.Combine(fixture.Dir.Path, "Program Files", "EA Desktop (x86)", "EA Desktop");
        Directory.CreateDirectory(second);
        fixture.Host.ClientPaths[ClientRegistryKey.EaDesktopWow6432] =
            Path.Combine(second, "EADesktop.exe");

        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);
        Assert.Equal(2, targets.Count);
        // The premise: two targets of one kind at two paths. Without it the test would pass for
        // the ordinary two-client reason.
        Assert.All(targets, t => Assert.Equal(ClientKind.EaApp, t.Client));
        foreach (var target in targets)
            Assert.True((await fixture.InstallAsync(target)).Success);

        Assert.True((await fixture.RemoveAsync(targets[1])).Success);

        Assert.True(Directory.Exists(fixture.Paths.ConfigDirectory));
        Assert.Equal(UnlockerState.Installed,
            (await fixture.Backend.GetStatusAsync(targets[0], CancellationToken.None)).State);
    }

    // The one production shape in which "no record" carries the restore on its own, and the one
    // where the value is otherwise lost for good. EA owns the backup but its record is corrupt, so
    // it can claim nothing; Origin is recorded, never owned the backup, and its removal sees EA's
    // unreadable record as "cannot tell", so it restores nothing and leaves the backup on disk.
    // EA's own removal then reads null - which is not the same as "not the owner" - and that is the
    // last claim left. Every other test reaching mayRestore also satisfies the "last removal in the
    // scope" disjunct, so this is the only one that fails when `record is null ||` is deleted.
    [Fact]
    public async Task A_corrupt_record_still_restores_the_autostart_value_after_a_sibling_left()
    {
        using var fixture = new InstallFixture();
        fixture.Host.Autostart(EaClientUnlockerBackend.AutostartValueName, @"C:\EA\EADesktop.exe");
        fixture.AddOrigin();
        var targets = await fixture.Backend.DetectTargetsAsync(CancellationToken.None);
        foreach (var target in targets)
            Assert.True((await fixture.InstallAsync(target)).Success);

        var own = Directory.GetFiles(fixture.App.UnlockerInstallDirectory)
            .First(f => Path.GetFileName(f).EndsWith("-eaapp.json", StringComparison.Ordinal));
        await File.WriteAllTextAsync(own, "{ not json");

        // Origin first, and it must leave both the value and the backup exactly as they are: this
        // is what makes EA's removal the last chance to put the value back.
        Assert.True((await fixture.RemoveAsync(targets[1])).Success);
        Assert.False(fixture.Host.AutostartValues.ContainsKey(
            EaClientUnlockerBackend.AutostartValueName));
        Assert.True(File.Exists(fixture.App.UnlockerAutostartBackupFile));

        Assert.True((await fixture.RemoveAsync(targets[0])).Success);

        Assert.True(fixture.Host.AutostartValues.TryGetValue(
            EaClientUnlockerBackend.AutostartValueName, out var restored));
        Assert.Equal(@"C:\EA\EADesktop.exe", restored.Value);
    }
}
