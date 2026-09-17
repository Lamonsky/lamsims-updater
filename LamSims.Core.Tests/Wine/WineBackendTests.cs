using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Logging;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

/// <summary>
/// A fake prefix with a client registered in it, the app's own root beside it, and the fake process
/// reader. No Wine, no network, no real-time sleep.
/// </summary>
public sealed class WineBackendFixture : IDisposable
{
    public PrefixFixture Prefix { get; }
    public AppPaths App { get; }
    public FakeWineProcesses Processes { get; } = new();
    public FakeDelayProvider Delays { get; } = new();
    public Notes Notes { get; } = new();

    /// <summary>
    /// Where the backend's own diagnostics go — a prefix with no client registered, a 32-bit
    /// prefix, a swept override record. None of those ask anything of the user, so none of them
    /// reach <see cref="Notes"/>, which the window renders as a list. Unused when a caller supplies
    /// its own sink; those tests assert against theirs.
    /// </summary>
    public RecordingLogSink Log { get; } = new();
    public List<UnlockerProgress> Reports { get; } = [];
    public WinePrefixUnlockerBackend Backend { get; }
    public IUnlockerAssetSource Assets { get; }
    public string ClientDirectory { get; }

    private const string EmptyBlock =
        "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Wine\\\\DllOverrides] 0\n";

    /// <summary>Convenience overload for a test that only cares about the log, at the default client.</summary>
    public WineBackendFixture(ILogSink log) : this(ClientKind.EaApp, "win64", log)
    {
    }

    public WineBackendFixture(ClientKind kind = ClientKind.EaApp, string arch = "win64",
                             ILogSink? log = null)
    {
        Prefix = new PrefixFixture(arch: arch);

        var windows = kind == ClientKind.EaApp
            ? @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe"
            : @"C:\Program Files\Origin\Origin.exe";
        ClientDirectory = Prefix.AddClient(kind, windows);

        var key = kind == ClientKind.EaApp
            ? @"Software\\Electronic Arts\\EA Desktop" : @"Software\\Origin";
        Prefix.WriteSystemReg($"WINE REGISTRY Version 2\n#arch={arch}\n\n[{key}] 0\n"
                              + $"\"ClientPath\"=\"{windows.Replace("\\", "\\\\")}\"\n");
        Prefix.WriteUserReg(EmptyBlock);

        Directory.CreateDirectory(Path.Combine(Prefix.DriveC, "ProgramData"));

        // DEVIATION from the brief: the install engine's machine.ini step only ever UPDATES an
        // existing file (IniFlagEditor.EditAsync returns false and warns when the file is absent,
        // per EaClientUnlockerBackend step 9) — it never creates one. Without this, no test can ever
        // observe the resolved machine.ini path, because the file the engine would touch never
        // exists. EaClientInstallTests.cs pre-seeds the same file for the same reason in
        // An_existing_machine_ini_gains_the_flag.
        var machineIniDirectory = Path.Combine(Prefix.DriveC, "ProgramData", "EA Desktop");
        Directory.CreateDirectory(machineIniDirectory);
        File.WriteAllText(Path.Combine(machineIniDirectory, "machine.ini"), "[machine]\n");

        App = new AppPaths(Path.Combine(Prefix.Dir.Path, "app"));
        App.EnsureCreated();

        var dll = Path.Combine(Prefix.Dir.Path, "cache", "version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllBytes(dll, [0x4D, 0x5A, 0x90, 0x00]);
        Assets = new StubAssetSource(dll);

        // The prefix is the only Wine home, so discovery finds exactly it.
        var home = Path.Combine(Prefix.Dir.Path, "home");
        Directory.CreateDirectory(Path.Combine(home, ".local", "share"));

        Backend = new WinePrefixUnlockerBackend(
            new LauncherHomes(home, null, null, null), App, Delays, Processes, "playday",
            () => Prefix.Root, Notes, log ?? Log);
    }

    public string UserReg => Path.Combine(Prefix.Root, "user.reg");
    public string InstalledDll => Path.Combine(ClientDirectory, "version.dll");

    public async Task<UnlockerTarget> TargetAsync() =>
        (await Backend.DetectTargetsAsync(CancellationToken.None))[0];

    public Task<UnlockerResult> InstallAsync(UnlockerTarget target) =>
        Backend.InstallAsync(target, Assets, new SyncProgress<UnlockerProgress>(Reports.Add),
                             CancellationToken.None);

    public Task<UnlockerResult> RemoveAsync(UnlockerTarget target) =>
        Backend.RemoveAsync(target, new SyncProgress<UnlockerProgress>(Reports.Add),
                            CancellationToken.None);

    /// <summary>
    /// A backend over an empty directory, with no prefix laid out and nothing detected. For the
    /// one assertion that must run on every platform: the fixture proper builds a Wine prefix on
    /// disk and is Linux-only, which is why the platform gate itself had no ungated test.
    /// </summary>
    public static WinePrefixUnlockerBackend Bare(string root)
    {
        var app = new AppPaths(Path.Combine(root, "app"));
        app.EnsureCreated();

        return new WinePrefixUnlockerBackend(
            new LauncherHomes(Path.Combine(root, "home"), null, null, null), app,
            new FakeDelayProvider(), new FakeWineProcesses(), "playday", () => null, new Notes(),
            NullLogSink.Instance);
    }

    public string ConfigDirectory =>
        Path.Combine(Prefix.DriveC, "users", "playday", "AppData", "Roaming", "anadius",
                     "EA DLC Unlocker v2");

    public void Dispose() => Prefix.Dispose();
}

public class WineBackendDetectionTests
{
    [LinuxFact]
    public async Task A_prefix_with_a_registered_client_yields_a_stamped_target()
    {
        using var f = new WineBackendFixture();

        var target = await f.TargetAsync();

        Assert.Equal("wine-prefix", target.BackendId);
        Assert.Equal(PathIdentity.Canonical(f.Prefix.Root), PathIdentity.Canonical(target.PrefixPath));
        Assert.NotNull(target.Environment);
        Assert.Equal(PathIdentity.Canonical(f.ClientDirectory),
                     PathIdentity.Canonical(target.ClientPath));
    }

    /// <summary>
    /// DisplayName is left exactly as the engine produced it. Rewriting it here to include the
    /// environment makes the Title rule — which composes DisplayName with Environment —
    /// unreachable, and the environment then appears twice or not at all.
    /// </summary>
    [LinuxFact]
    public async Task The_display_name_is_not_rewritten()
    {
        using var f = new WineBackendFixture();

        var target = await f.TargetAsync();

        Assert.Equal("EA app", target.DisplayName);
        Assert.DoesNotContain("Wine", target.DisplayName);
    }

    /// <summary>
    /// The exact string, and the reason WineUnlockerHost tracks SawClientValue: "no client is
    /// registered here" and "one is registered and its path is gone" are different facts about the
    /// prefix, and the engine only ever returns a list. Both go to the log — most prefixes on a
    /// Linux machine have no EA client in them, so one note per prefix is exactly the flood that
    /// buried the unlocker's own controls.
    /// </summary>
    [LinuxFact]
    public async Task A_prefix_with_no_client_is_reported_and_yields_nothing()
    {
        using var f = new WineBackendFixture();
        f.Prefix.WriteSystemReg("WINE REGISTRY Version 2\n#arch=win64\n");

        Assert.Empty(await f.Backend.DetectTargetsAsync(CancellationToken.None));
        Assert.True(f.Log.Logged("no EA app or Origin is registered"),
                    string.Join("\n", f.Log.Texts));
    }

    /// <summary>
    /// The arch check is a PER-TARGET filter, not a prefix rejection: Origin is 32-bit and works in
    /// either, so a win32 prefix with Origin registered must still yield a target and must NOT emit
    /// the note. Asserting only the EA app case passes against an implementation that rejects the
    /// whole prefix.
    /// </summary>
    [LinuxFact]
    public async Task A_32_bit_prefix_rejects_the_ea_app_and_keeps_origin()
    {
        using (var ea = new WineBackendFixture(ClientKind.EaApp, arch: "win32"))
        {
            Assert.Empty(await ea.Backend.DetectTargetsAsync(CancellationToken.None));
            Assert.True(ea.Log.Logged("is 32-bit and the EA app needs a 64-bit prefix.",
                                      LogSeverity.Warning),
                        string.Join("\n", ea.Log.Texts));
        }

        using var origin = new WineBackendFixture(ClientKind.Origin, arch: "win32");

        Assert.Single(await origin.Backend.DetectTargetsAsync(CancellationToken.None));

        // The LOG, not the notes: the backend writes this diagnostic with _log.Write, and nothing
        // under Unlocking/Wine ever writes "32-bit" to the notes channel. Asserted against the
        // notes, this could not fail, and a backend that kept the Origin target while also warning
        // about it would pass.
        Assert.False(origin.Log.Logged("32-bit"), string.Join("\n", origin.Log.Texts));
    }

    [LinuxFact]
    public async Task Both_clients_in_one_prefix_yield_two_targets()
    {
        using var f = new WineBackendFixture();
        var origin = f.Prefix.AddClient(ClientKind.Origin, @"C:\Program Files\Origin\Origin.exe");
        f.Prefix.WriteSystemReg(
            "WINE REGISTRY Version 2\n#arch=win64\n\n"
            + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
            + "\"ClientPath\"=\"C:\\\\Program Files\\\\Electronic Arts\\\\EA Desktop\\\\EA Desktop\\\\EADesktop.exe\"\n\n"
            + "[Software\\\\Origin] 0\n\"ClientPath\"=\"C:\\\\Program Files\\\\Origin\\\\Origin.exe\"\n");

        var targets = await f.Backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Equal(2, targets.Count);
        Assert.Contains(PathIdentity.Canonical(origin),
                        targets.Select(t => PathIdentity.Canonical(t.ClientPath)));
    }

    /// <summary>
    /// A record for a prefix that is gone would make the next removal write a stale value into a
    /// freshly generated user.reg, so detection is where it is caught.
    /// </summary>
    [LinuxFact]
    public async Task Detection_sweeps_a_record_whose_prefix_has_vanished()
    {
        using var f = new WineBackendFixture();
        var store = new WineOverrideStore(f.App);
        var gone = Path.Combine(f.Prefix.Dir.Path, "was-a-prefix");
        await store.WriteAsync(new WineOverrideRecord(gone, true, null, false, null),
                               CancellationToken.None);

        await f.Backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Null(store.Read(gone));
        Assert.True(f.Log.Logged("no longer exists"), string.Join("\n", f.Log.Texts));
    }

    /// <summary>
    /// Linux only, not "not Windows": macOS is out of scope because it has no
    /// <c>/proc</c>, so the liveness check this backend depends on can never fire there.
    /// </summary>
    [LinuxFact]
    public void The_backend_is_supported_only_on_linux()
    {
        using var f = new WineBackendFixture();

        // The constant this attribute guarantees, not OperatingSystem.IsLinux(): under [LinuxFact]
        // both sides of that comparison are true, so it held for a property whose body was simply
        // `true` - which would offer the backend on Windows and macOS, where the /proc liveness
        // gate protecting a user.reg write can never fire.
        Assert.True(f.Backend.IsSupported);
        Assert.Equal("wine-prefix", f.Backend.Id);
    }

    /// <summary>
    /// Ungated, and without the Linux-only prefix fixture, so it runs on all three CI legs.
    ///
    /// It cannot fail on Linux: there OperatingSystem.IsLinux() and a hard-coded `true` agree, and
    /// this assertion is the same shape the [LinuxFact] above used to have. Its value is on the
    /// Windows and macOS legs, where a backend that dropped its platform gate would offer itself
    /// for a user.reg write it cannot guard - the liveness check reads /proc, which is not there.
    /// So a green run of this test locally proves nothing; a green run of the matrix does.
    /// </summary>
    [Fact]
    public void The_backend_reports_support_for_this_platform_only()
    {
        using var temp = new TempDir();

        var backend = WineBackendFixture.Bare(temp.Path);

        Assert.Equal(OperatingSystem.IsLinux(), backend.IsSupported);
    }
}

public class WineBackendOperationTests
{
    /// <summary>
    /// The whole point: the DLL lands at the RESOLVED path inside the prefix and the starred entry
    /// appears in user.reg. Either alone is not an install.
    /// </summary>
    [LinuxFact]
    public async Task An_install_writes_the_dll_and_the_override()
    {
        using var f = new WineBackendFixture();
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(f.InstalledDll));
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);
    }

    /// <summary>
    /// The two roots, resolved rather than joined. machine.ini has to land inside the prefix's
    /// own ProgramData and the staged copy beside the resolved client directory; a string join puts
    /// them in the host filesystem's root, or nowhere.
    /// </summary>
    [LinuxFact]
    public async Task The_configuration_and_staged_copy_land_inside_the_prefix()
    {
        using var f = new WineBackendFixture();

        Assert.True((await f.InstallAsync(await f.TargetAsync())).Success);

        Assert.True(File.Exists(Path.Combine(f.ConfigDirectory, "config.ini")));

        // Existence alone is vacuous here: the fixture itself seeds machine.ini (the engine only
        // ever UPDATES an existing one, never creates it), so an existence check would pass even if
        // PathsFor resolved CommonAppData to the wrong place entirely. Asserting the flag was ADDED
        // proves the engine actually opened and edited this exact resolved file, the way
        // EaClientInstallTests' own An_existing_machine_ini_gains_the_flag does for the same reason.
        Assert.Contains("machine.bgsstandaloneenabled=0", await File.ReadAllTextAsync(
            Path.Combine(f.Prefix.DriveC, "ProgramData", "EA Desktop", "machine.ini")));

        Assert.True(File.Exists(Path.Combine(
            Path.GetDirectoryName(f.ClientDirectory)!, "StagedEADesktop", "EA Desktop",
            "version.dll")));
    }

    /// <summary>
    /// The progress contract. The count alone passes against a run that never reaches the end,
    /// the final value alone passes against a run that skipped a number, and Total alone passes
    /// against a run that reported the inner engine's total — so all three are asserted. The
    /// engine's own trailing Total/Total report must be dropped, or there is one report too few and
    /// two at the same count.
    /// </summary>
    [LinuxTheory]
    [InlineData(ClientKind.EaApp, 12)]
    [InlineData(ClientKind.Origin, 10)]
    public async Task Install_progress_runs_from_zero_to_the_wrapped_total(ClientKind kind, int total)
    {
        using var f = new WineBackendFixture(kind);

        var result = await f.InstallAsync(await f.TargetAsync());
        Assert.True(result.Success, result.Error);

        Assert.All(f.Reports, r => Assert.Equal(total, r.Total));
        Assert.Equal(total + 1, f.Reports.Count);
        Assert.Equal(Enumerable.Range(0, total + 1), f.Reports.Select(r => r.Completed));
    }

    [LinuxTheory]
    [InlineData(ClientKind.EaApp, 11)]
    [InlineData(ClientKind.Origin, 8)]
    public async Task Removal_progress_runs_from_zero_to_the_wrapped_total(ClientKind kind, int total)
    {
        using var f = new WineBackendFixture(kind);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        f.Reports.Clear();

        var result = await f.RemoveAsync(target);
        Assert.True(result.Success, result.Error);

        Assert.All(f.Reports, r => Assert.Equal(total, r.Total));
        Assert.Equal(total + 1, f.Reports.Count);
        Assert.Equal(Enumerable.Range(0, total + 1), f.Reports.Select(r => r.Completed));
    }

    /// <summary>
    /// The first of three cases, and the one a suite with only the refusal would miss: a
    /// live prefix whose override is ALREADY correct needs no write, so the install proceeds. Test
    /// only the refusal and "refuse whenever live" passes, which breaks every ordinary reinstall.
    /// </summary>
    [LinuxFact]
    public async Task A_live_prefix_whose_override_is_already_correct_installs()
    {
        using var f = new WineBackendFixture();
        f.Prefix.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                              + "[Software\\\\Wine\\\\DllOverrides] 0\n"
                              + "\"*version\"=\"native,builtin\"\n");
        f.Processes.Live = true;

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(f.InstalledDll));
    }

    [LinuxFact]
    public async Task A_live_prefix_whose_launchers_supply_the_override_installs()
    {
        using var f = new WineBackendFixture();
        var games = Path.Combine(f.Prefix.Dir.Path, "home", ".local", "share", "lutris", "games");
        Directory.CreateDirectory(games);
        File.WriteAllText(Path.Combine(games, "a.yml"),
                          $"name: EA app\nprefix: {f.Prefix.Root}\nwine:\n  overrides:\n    version.dll: n,b\n");
        f.Processes.Live = true;

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(f.InstalledDll));
    }

    /// <summary>
    /// The third case: a write is needed and the prefix is live, so the install refuses BEFORE it
    /// writes anything. The DLL must not be on disk, or the user has a half-install behind a
    /// failure message.
    /// </summary>
    [LinuxFact]
    public async Task A_live_prefix_that_needs_a_write_refuses_before_touching_anything()
    {
        using var f = new WineBackendFixture();
        f.Processes.Live = true;

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.False(result.Success);
        Assert.Contains("Close the EA app", result.Error);
        Assert.False(File.Exists(f.InstalledDll));
        Assert.False(Directory.Exists(f.ConfigDirectory));
    }

    /// <summary>
    /// The fourth case, and the one the three above cannot reach: a REPAIR, where the record
    /// survives but the entry it describes has gone — a Proton update regenerating user.reg, or
    /// winecfg's Libraries tab. A write is still needed, so a live prefix has to refuse here
    /// exactly as it does for a first install. A gate that also requires the record to be absent
    /// lets this one write through with no liveness check in front of it, and it read-modify-writes
    /// the whole of user.reg while wineserver is free to flush over it. Both halves are asserted:
    /// the refusal, and that the entry is still absent afterwards — the refusal alone would pass
    /// against an implementation that wrote first and reported the failure second.
    /// </summary>
    [LinuxFact]
    public async Task A_live_prefix_needing_a_repair_write_refuses_like_a_first_install()
    {
        using var f = new WineBackendFixture();
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        // The entry goes; the record stays, which is what makes this a repair rather than a first
        // install.
        await WineRegistryFile.RemoveValueAsync(f.UserReg, WineDllOverride.Key, "*version", false,
                                                CancellationToken.None);
        Assert.Null(WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version"));

        f.Processes.Live = true;

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.Contains("Close the EA app", result.Error);
        Assert.Null(WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version"));
    }

    /// <summary>
    /// The same refusal, for Origin. The message must name Origin and must NOT mention the EA app:
    /// a user unlocking Origin in a live prefix has no reason to be told to close a different
    /// application. Both halves are asserted, or a message naming both would pass.
    /// </summary>
    [LinuxFact]
    public async Task A_live_prefix_that_needs_a_write_for_origin_names_origin_not_the_ea_app()
    {
        using var f = new WineBackendFixture(ClientKind.Origin);
        f.Processes.Live = true;

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.False(result.Success);
        Assert.Contains("Close Origin", result.Error);
        Assert.DoesNotContain("EA app", result.Error);
        Assert.False(File.Exists(f.InstalledDll));
        Assert.False(Directory.Exists(f.ConfigDirectory));
    }

    /// <summary>
    /// Warning, not failure. unlink+rename succeeds with a file mapped into a live process, so the
    /// install really did work and the only thing left is a restart. The reachable combination is
    /// the one this test builds: a live wineserver, a running client, AND the override already
    /// correct — because a write with the prefix live would have refused instead.
    /// </summary>
    [LinuxFact]
    public async Task A_running_client_yields_a_restart_warning_and_not_a_failure()
    {
        using var f = new WineBackendFixture();
        f.Prefix.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                              + "[Software\\\\Wine\\\\DllOverrides] 0\n"
                              + "\"*version\"=\"native,builtin\"\n");
        f.Processes.Live = true;
        f.Processes.Clients.Add("EADesktop.exe");

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings!, w => w.Contains("Restart it", StringComparison.Ordinal));
    }

    /// <summary>
    /// The prefix goes live between step 1's check and step 12's write, which is a real window: the
    /// fetch takes seconds. The DLL is already on disk, so this is a failure with a different
    /// message and re-running is idempotent.
    /// </summary>
    [LinuxFact]
    public async Task A_prefix_that_goes_live_mid_install_fails_with_the_dll_in_place()
    {
        using var f = new WineBackendFixture();
        var target = await f.TargetAsync();

        // Goes live once the asset fetch has happened, which is exactly where the real window is.
        ((StubAssetSource)f.Assets).OnRead = () => f.Processes.Live = true;

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.Contains("could not be set", result.Error);
        Assert.True(File.Exists(f.InstalledDll));
    }

    /// <summary>
    /// Hostile launcher overrides warn and still install: the registry entry helps every other
    /// launch path. The warning names the file and the game because that launch path cannot be
    /// fixed from here.
    /// </summary>
    [LinuxFact]
    public async Task A_hostile_launcher_override_warns_and_still_installs()
    {
        using var f = new WineBackendFixture();
        var games = Path.Combine(f.Prefix.Dir.Path, "home", ".local", "share", "lutris", "games");
        Directory.CreateDirectory(games);
        File.WriteAllText(Path.Combine(games, "nfs.yml"),
                          $"name: NFS Unbound\nprefix: {f.Prefix.Root}\nwine:\n  overrides:\n    version.dll: b,n\n");

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings!, w => w.Contains("NFS Unbound", StringComparison.Ordinal));
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);
    }

    [LinuxFact]
    public async Task An_app_defaults_override_is_reported_as_a_warning()
    {
        using var f = new WineBackendFixture();
        f.Prefix.WriteUserReg("WINE REGISTRY Version 2\n#arch=win64\n\n"
                              + "[Software\\\\Wine\\\\AppDefaults\\\\EADesktop.exe\\\\DllOverrides] 0\n"
                              + "\"*version\"=\"builtin\"\n");

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings!, w => w.Contains("per-application", StringComparison.Ordinal));
    }

    /// <summary>
    /// Removal checks liveness BEFORE it mutates anything. An order that put the extra step last
    /// would delete version.dll and only then discover the override could not be cleared, leaving
    /// the DLL gone, the override set and the record on disk. Asserted by the DLL still being
    /// present.
    /// </summary>
    [LinuxFact]
    public async Task Removal_on_a_live_prefix_refuses_before_deleting_the_dll()
    {
        using var f = new WineBackendFixture();
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        f.Processes.Live = true;
        var result = await f.RemoveAsync(target);

        Assert.False(result.Success);
        Assert.Contains("Nothing has been removed.", result.Error);
        Assert.True(File.Exists(f.InstalledDll));
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);
    }

    /// <summary>
    /// Install, install again, remove leaves user.reg byte-identical. This is the assertion that
    /// catches a second install recording our own value as the prior one — after which removal
    /// "restores" the override and the unlocker keeps loading.
    /// </summary>
    [LinuxFact]
    public async Task Install_twice_then_remove_leaves_user_reg_as_it_was()
    {
        using var f = new WineBackendFixture();
        var before = await File.ReadAllTextAsync(f.UserReg);
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);
        Assert.True((await f.InstallAsync(target)).Success);
        Assert.True((await f.RemoveAsync(target)).Success);

        Assert.Equal(before, await File.ReadAllTextAsync(f.UserReg));
        Assert.False(File.Exists(f.InstalledDll));
    }

    /// <summary>
    /// Install, then a user adds a launcher override, then remove: our line is GONE. Removal branches
    /// on the record, never on what the launchers now say, or the registry write outlives the
    /// unlocker for ever.
    /// </summary>
    [LinuxFact]
    public async Task A_launcher_override_added_after_installing_does_not_keep_our_line()
    {
        using var f = new WineBackendFixture();
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        var games = Path.Combine(f.Prefix.Dir.Path, "home", ".local", "share", "lutris", "games");
        Directory.CreateDirectory(games);
        File.WriteAllText(Path.Combine(games, "a.yml"),
                          $"prefix: {f.Prefix.Root}\nwine:\n  overrides:\n    version.dll: n,b\n");

        Assert.True((await f.RemoveAsync(target)).Success);

        Assert.Null(WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version"));
    }

    /// <summary>
    /// One "*version" entry serves every client in the prefix, so clearing it while the other
    /// client's version.dll is still on disk leaves that DLL loading and doing nothing — the
    /// shared-configuration hazard in a new place. The record survives too, so the sibling's own
    /// removal can undo it.
    /// </summary>
    [LinuxFact]
    public async Task Removing_one_client_keeps_the_override_while_a_sibling_is_installed()
    {
        using var f = new WineBackendFixture();
        var origin = f.Prefix.AddClient(ClientKind.Origin, @"C:\Program Files\Origin\Origin.exe");
        f.Prefix.WriteSystemReg(
            "WINE REGISTRY Version 2\n#arch=win64\n\n"
            + "[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
            + "\"ClientPath\"=\"C:\\\\Program Files\\\\Electronic Arts\\\\EA Desktop\\\\EA Desktop\\\\EADesktop.exe\"\n\n"
            + "[Software\\\\Origin] 0\n\"ClientPath\"=\"C:\\\\Program Files\\\\Origin\\\\Origin.exe\"\n");

        var targets = await f.Backend.DetectTargetsAsync(CancellationToken.None);
        foreach (var target in targets) Assert.True((await f.InstallAsync(target)).Success);

        var ea = targets.First(t => t.Client == ClientKind.EaApp);
        Assert.True((await f.RemoveAsync(ea)).Success);

        // The sibling's DLL is still there, so the override that makes it load must be too.
        Assert.True(File.Exists(Path.Combine(origin, "version.dll")));
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);

        // And removing the sibling finally clears it.
        Assert.True((await f.RemoveAsync(targets.First(t => t.Client == ClientKind.Origin))).Success);
        Assert.Null(WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version"));
    }

    /// <summary>
    /// When the prefix kind cannot say which Windows user the DLL will read, the
    /// configuration is written under every non-Public one. Getting this wrong is a silent no-op
    /// behind a green UI.
    /// </summary>
    [LinuxFact]
    public async Task An_ambiguous_prefix_gets_the_configuration_under_every_user()
    {
        using var f = new WineBackendFixture();
        Directory.Delete(Path.Combine(f.Prefix.DriveC, "users", "playday"), recursive: true);
        f.Prefix.AddUser("steamuser");
        f.Prefix.AddUser("crossover");

        Assert.True((await f.InstallAsync(await f.TargetAsync())).Success);

        foreach (var user in new[] { "steamuser", "crossover" })
        {
            Assert.True(File.Exists(Path.Combine(f.Prefix.DriveC, "users", user, "AppData",
                                                 "Roaming", "anadius", "EA DLC Unlocker v2",
                                                 "config.ini")),
                        $"no configuration under {user}");
        }
    }

    /// <summary>
    /// A secondary Windows user directory Mirror cannot reach is the ambiguous case where silence
    /// matters most, since that user's launch path is left without the configuration the client
    /// reads. It must warn rather than skip with a bare `continue`, and still finish the install
    /// successfully for the primary user it did reach.
    /// </summary>
    [LinuxFact]
    public async Task An_unreachable_secondary_user_is_warned_about_rather_than_silently_skipped()
    {
        using var f = new WineBackendFixture();
        Directory.Delete(Path.Combine(f.Prefix.DriveC, "users", "playday"), recursive: true);
        f.Prefix.AddUser("steamuser");
        var broken = f.Prefix.AddUser("z-broken");

        // "steamuser" sorts first (Ordinal) and becomes primary; z-broken is the one Mirror has to
        // reach, and it cannot: no AppData directory at all.
        Directory.Delete(Path.Combine(broken, "AppData"), recursive: true);

        var result = await f.InstallAsync(await f.TargetAsync());

        Assert.True(result.Success);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("z-broken") && w.Contains("not copied"));

        // The reachable user still got its configuration.
        Assert.True(File.Exists(Path.Combine(f.Prefix.DriveC, "users", "steamuser", "AppData",
                                             "Roaming", "anadius", "EA DLC Unlocker v2",
                                             "config.ini")));
    }

    [LinuxFact]
    public async Task Removal_takes_the_mirrored_configuration_with_it()
    {
        using var f = new WineBackendFixture();
        Directory.Delete(Path.Combine(f.Prefix.DriveC, "users", "playday"), recursive: true);
        f.Prefix.AddUser("steamuser");
        f.Prefix.AddUser("crossover");
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);
        Assert.True((await f.RemoveAsync(target)).Success);

        foreach (var user in new[] { "steamuser", "crossover" })
        {
            Assert.False(Directory.Exists(Path.Combine(f.Prefix.DriveC, "users", user, "AppData",
                                                       "Roaming", "anadius", "EA DLC Unlocker v2")),
                         $"configuration left under {user}");
        }
    }

    [LinuxFact]
    public async Task Removal_on_a_vanished_prefix_writes_nothing()
    {
        using var f = new WineBackendFixture();
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        Directory.Delete(f.Prefix.Root, recursive: true);

        var result = await f.RemoveAsync(target);

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
    }
}
