using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;
using LamSims.Core.Logging;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

/// <summary>
/// Wraps <see cref="StaticUnlockerAssetSource"/> over a <see cref="TestFileServer"/> with a pin
/// pointing at it, the same shape <see cref="StaticUnlockerAssetSourceTests"/> builds inline. The
/// server is started synchronously in the constructor (blocking on the async start) so a test can
/// write the ordinary <c>using var f = new AssetFixture(log);</c> rather than an async factory.
/// </summary>
public sealed class AssetFixture : IDisposable
{
    private static readonly byte[] Payload =
        Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

    public TempDir Dir { get; } = new();
    public TestFileServer Server { get; }
    public DownloadPaths Paths { get; }
    public StaticUnlockerAssetSource Source { get; }

    public AssetFixture(ILogSink? log = null)
    {
        Server = TestFileServer.StartAsync(Payload).GetAwaiter().GetResult();
        Paths = new DownloadPaths(Dir.Path);
        var pins = new Dictionary<ClientKind, AssetPin>
        {
            [ClientKind.EaApp] = new(Server.FileUrl.ToString(), Payload.Length,
                                     Digest(Payload), "ea_app_version.dll"),
        };
        Source = new StaticUnlockerAssetSource(new HttpClient(), Paths, pins, log: log);
    }

    /// <summary>Writes the pinned bytes straight into the cache, so the next fetch is a reuse.</summary>
    public async Task SeedCacheAsync()
    {
        Paths.EnsureCreated();
        await File.WriteAllBytesAsync(Paths.UnlockerAssetFile("ea_app_version.dll"), Payload);
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        Server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Dir.Dispose();
    }
}

public class UnlockerLoggingTests
{
    [Fact]
    public async Task An_install_logs_every_step_it_reports()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        // The engine reports InstallStepsEaApp = 10 steps; each must appear exactly once, in
        // order. Zipped against the expected 1..10 sequence rather than counted and spot-checked
        // at the ends: a count-and-endpoints check alone would still pass a run that duplicated
        // "Step 3/10" in place of a missing "Step 4/10".
        var stepLines = log.Lines
            .Where(l => l.Text.StartsWith("Step ", StringComparison.Ordinal))
            .Select(l => l.Text)
            .ToArray();
        var expectedPrefixes = Enumerable.Range(1, 10).Select(i => $"Step {i}/10: ").ToArray();
        Assert.Equal(expectedPrefixes.Length, stepLines.Length);
        Assert.All(expectedPrefixes.Zip(stepLines),
            pair => Assert.StartsWith(pair.First, pair.Second, StringComparison.Ordinal));
    }

    /// <summary>
    /// The three autostart outcomes are the ones a user asks about afterwards — "did it put my
    /// EA app launch entry back?" — and each takes a different branch on removal.
    /// </summary>
    [Fact]
    public async Task The_autostart_entry_is_logged_when_it_is_recorded_and_removed()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.True(log.Logged("Autostart entry recorded and removed"));
    }

    [Fact]
    public async Task A_cached_dll_says_it_was_reused_rather_than_fetched()
    {
        // Seeds the cache with the pinned bytes, then asserts the source says so instead of
        // silently doing nothing observable.
        var log = new RecordingLogSink();
        using var f = new AssetFixture(log);
        await f.SeedCacheAsync();

        await f.Source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.True(log.Logged("Reusing the cached DLL"));
        Assert.False(log.Logged("Fetching the unlocker DLL"));
    }

    // The counterpart to the cache-reuse test above: without a cache entry, GetDllAsync must say
    // it went to fetch the DLL rather than leaving the decision unobserved.
    [Fact]
    public async Task A_dll_that_is_not_cached_is_logged_as_fetched()
    {
        var log = new RecordingLogSink();
        using var f = new AssetFixture(log);

        var bytes = await f.Source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.NotEmpty(bytes.ToArray());
        Assert.True(log.Logged("Fetching the unlocker DLL for EaApp"));
        Assert.False(log.Logged("Reusing the cached DLL"));
    }

    // Detection is the one place a user learns which client was found at all; a log line missing
    // here means a support conversation has nothing to go on but "it didn't work".
    [Fact]
    public async Task Detection_is_logged_for_each_target_found()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);

        var target = await f.TargetAsync();

        Assert.True(log.Logged($"Detected {target.DisplayName} at {target.ClientPath}"));
    }

    [Fact]
    public async Task The_autostart_entry_is_logged_when_restored_on_removal()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        Assert.True((await f.RemoveAsync(target)).Success);

        Assert.True(log.Logged("Autostart entry restored", LogSeverity.Info));
    }

    // The client can re-create its own Run entry between install and removal; restoring the
    // recorded value over that would downgrade it, so removal leaves it alone and warns.
    [Fact]
    public async Task The_autostart_entry_re_created_before_removal_is_logged_as_left_alone()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        // Stand in for the client re-creating its own entry before the unlocker is removed.
        f.Host.Autostart("EADM", @"C:\EA\new.exe");

        Assert.True((await f.RemoveAsync(target)).Success);

        Assert.True(log.Logged("Autostart entry had been re-created, so it was left as it is",
            LogSeverity.Warning));
    }

    // Upstream deletes a non-text Run value type-blind on install; removal cannot write it back as
    // text without changing what the client reads, so it warns instead of guessing.
    [Fact]
    public async Task An_unsupported_autostart_value_cannot_be_restored_and_is_logged_as_a_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", "1", AutostartValueKind.Unsupported);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        Assert.True((await f.RemoveAsync(target)).Success);

        Assert.True(log.Logged("Autostart entry was not a text value, so it could not be put back",
            LogSeverity.Warning));
    }

    // Both step-6 warnings at once: a stale file the directory refuses to unlink, and the later
    // .ini listing that the same lost permission also breaks.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Old_unlocker_files_that_cannot_be_removed_are_logged_as_warnings()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        await File.WriteAllTextAsync(Path.Combine(target.ClientPath, "version_o.dll"), "old");

        // Execute only: File.Exists/Delete can still traverse to a known name, but neither the
        // unlink (needs write) nor Directory.GetFiles (needs read) can succeed.
        const UnixFileMode locked = UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(target.ClientPath);
        File.SetUnixFileMode(target.ClientPath, locked);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(target.ClientPath, unlocked);
        }

        // Step 7 (copying version.dll) needs the same write permission, so this install fails
        // there; the two step-6 warnings collected before that are still on the failed result.
        Assert.False(result.Success);
        Assert.True(log.Logged("'version_o.dll' could not be removed", LogSeverity.Warning));
        Assert.True(log.Logged("Older unlocker .ini files could not be listed", LogSeverity.Warning));
    }

    // The StagedEADesktop copy is non-fatal: without it the install still succeeds today and only
    // stops working after the next EA app self-update, so a refused parent directory must warn
    // rather than fail the install.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_staged_copy_that_cannot_be_created_is_logged_as_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        var parent = Directory.GetParent(target.ClientPath)!.FullName;

        // Write-denied only: the client directory itself, a child of parent, stays reachable and
        // writable, so only creating the new StagedEADesktop sibling fails.
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(parent);
        File.SetUnixFileMode(parent, locked);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(parent, unlocked);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("The StagedEADesktop copy failed", LogSeverity.Warning));
    }

    [Fact]
    public async Task A_missing_machine_ini_is_logged_as_a_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("machine.ini was not found", LogSeverity.Warning));
    }

    // Distinct from the "not found" case above: here machine.ini exists and the edit itself is
    // refused, which is the exception branch rather than the missing-file branch.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_machine_ini_that_cannot_be_updated_is_logged_as_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var iniDirectory = Path.GetDirectoryName(f.Paths.MachineIniFile)!;
        Directory.CreateDirectory(iniDirectory);
        await File.WriteAllTextAsync(f.Paths.MachineIniFile, "[machine]\nfoo=1\n");
        var target = await f.TargetAsync();

        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(iniDirectory);
        File.SetUnixFileMode(iniDirectory, locked);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(iniDirectory, unlocked);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("machine.ini could not be updated", LogSeverity.Warning));
    }

    // The autostart value is one machine-wide entry; a second client that finds it live but the
    // backup already claimed by another install must leave it alone and warn, not overwrite the
    // owning backup.
    [Fact]
    public async Task A_second_client_that_finds_the_backup_already_owned_logs_the_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart(EaClientUnlockerBackend.AutostartValueName, @"C:\EA\EADesktop.exe");
        f.AddOrigin();
        var targets = await f.Backend.DetectTargetsAsync(CancellationToken.None);
        Assert.True((await f.InstallAsync(targets[0])).Success);

        // Stand in for the value coming back before the second client installs.
        f.Host.Autostart(EaClientUnlockerBackend.AutostartValueName, @"C:\EA\EADesktop.exe");

        Assert.True((await f.InstallAsync(targets[1])).Success);

        Assert.True(log.Logged("Another install of the unlocker already owns the saved autostart",
            LogSeverity.Warning));
    }

    // A group-policy-restricted Run key throws SecurityException, which the host does not catch;
    // the backend's own filter has to, and log the non-fatal warning it turns into.
    [Fact]
    public async Task An_autostart_write_the_policy_forbids_is_logged_as_a_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        f.Host.ThrowSecurityOnAutostartWrite = true;
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("The autostart entry could not be changed", LogSeverity.Warning));
    }

    // Written last, after every other install step; a refused write here must still warn rather
    // than escape, since version.dll is already on disk by this point.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_install_record_that_cannot_be_written_is_logged_as_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();

        // No autostart value is set, so the earlier autostart step never touches App.Root itself
        // and this lock isolates the install-record write.
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(f.App.Root);
        File.SetUnixFileMode(f.App.Root, locked);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(f.App.Root, unlocked);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("The install record could not be written", LogSeverity.Warning));
    }

    // A scheduled task left by an older install still has to be removed on every upgrade; a task
    // this build never created but cannot query must warn instead of failing removal outright.
    [Fact]
    public async Task A_scheduled_task_that_cannot_be_removed_is_logged_as_a_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        f.Host.ThrowOnDeleteScheduledTask = true;

        var result = await f.RemoveAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("scheduled task could not be removed", LogSeverity.Warning));
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_staged_cleanup_failure_on_removal_is_logged_as_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        var inner = Path.Combine(Directory.GetParent(target.ClientPath)!.FullName,
            "StagedEADesktop", "EA Desktop");
        Assert.True(Directory.Exists(inner));

        // Execute only: the staged DLL cannot be listed (needs read) or unlinked (needs write).
        const UnixFileMode locked = UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(inner);
        File.SetUnixFileMode(inner, locked);
        UnlockerResult result;
        try
        {
            result = await f.RemoveAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(inner, unlocked);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("StagedEADesktop could not be cleaned up", LogSeverity.Warning));
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_machine_ini_that_cannot_be_restored_on_removal_is_logged_as_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var iniDirectory = Path.GetDirectoryName(f.Paths.MachineIniFile)!;
        Directory.CreateDirectory(iniDirectory);
        await File.WriteAllTextAsync(f.Paths.MachineIniFile, "[machine]\nfoo=1\n");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);
        Assert.Contains("machine.bgsstandaloneenabled=0",
            await File.ReadAllTextAsync(f.Paths.MachineIniFile));

        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(iniDirectory);
        File.SetUnixFileMode(iniDirectory, locked);
        UnlockerResult result;
        try
        {
            result = await f.RemoveAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(iniDirectory, unlocked);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("machine.ini could not be restored", LogSeverity.Warning));
    }

    // Leaving a corrupt backup on disk would keep step 1 finding something to remove forever, so
    // an unreadable backup has to be discarded and the fact logged, not silently dropped.
    [Fact]
    public async Task An_unreadable_autostart_backup_is_logged_as_a_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        await File.WriteAllTextAsync(f.App.UnlockerAutostartBackupFile, "{ not json");

        Assert.True((await f.RemoveAsync(target)).Success);

        Assert.True(log.Logged("The autostart backup could not be read, so it was discarded",
            LogSeverity.Warning));
    }

    [Fact]
    public async Task An_incomplete_autostart_backup_is_logged_as_a_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        await File.WriteAllTextAsync(f.App.UnlockerAutostartBackupFile,
            """{"Name":"","Value":"x","Kind":0}""");

        Assert.True((await f.RemoveAsync(target)).Success);

        Assert.True(log.Logged("The autostart backup was incomplete, so it was discarded",
            LogSeverity.Warning));
    }

    // The outer catch around the whole restore block, distinct from the inner JSON-parse catch:
    // triggered here by the backup file surviving to the final delete but that delete being
    // refused, after the value has already been written back.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_autostart_restore_failure_is_logged_as_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Autostart("EADM", @"C:\EA\EADesktop.exe");
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(f.App.Root);
        File.SetUnixFileMode(f.App.Root, locked);
        UnlockerResult result;
        try
        {
            result = await f.RemoveAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(f.App.Root, unlocked);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("The autostart entry could not be restored", LogSeverity.Warning));
    }

    // The store answering "cannot tell" must not be treated as "nobody else", so the folder stays
    // and the reason is logged rather than left only in the result's Warnings.
    [Fact]
    public async Task An_unprovable_sibling_state_is_logged_as_a_warning()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.AddOrigin();
        var targets = await f.Backend.DetectTargetsAsync(CancellationToken.None);
        foreach (var t in targets) Assert.True((await f.InstallAsync(t)).Success);

        var sibling = Directory.GetFiles(f.App.UnlockerInstallDirectory)
            .First(p => Path.GetFileName(p).EndsWith("-origin.json", StringComparison.Ordinal));
        await File.WriteAllTextAsync(sibling, "{ not json");

        var result = await f.RemoveAsync(targets[0]);

        Assert.True(result.Success);
        Assert.True(log.Logged("could not be determined", LogSeverity.Warning));
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_configuration_folder_that_cannot_be_removed_is_logged_as_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        var parent = Directory.GetParent(f.Paths.ConfigDirectory)!.FullName;
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(parent);
        File.SetUnixFileMode(parent, locked);
        UnlockerResult result;
        try
        {
            result = await f.RemoveAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(parent, unlocked);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(log.Logged("The unlocker configuration folder could not be removed",
            LogSeverity.Warning));
    }

    // WinePrefixUnlockerBackend logs only the prefix line; its step lines are the ones the inner
    // EaClientUnlockerBackend already logs, which is why the inner engine has to receive the same
    // sink rather than a fresh NullLogSink.
    [LinuxFact]
    public async Task Installing_through_a_wine_prefix_logs_which_prefix_is_used()
    {
        var log = new RecordingLogSink();
        using var f = new WineBackendFixture(log);
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.True(log.Logged($"Using Wine prefix {f.Prefix.Root}"));
        // The inner engine's own step lines must reach the same sink, not a NullLogSink of its own.
        Assert.True(log.Logged("Step 1/", LogSeverity.Info));
    }

    // RemoveAsync's own prefix line is a separate statement from InstallAsync's, and fires before
    // the inner engine's own "nothing to remove" early exit, so no prior install is needed here.
    [LinuxFact]
    public async Task Removing_through_a_wine_prefix_logs_which_prefix_is_used()
    {
        var log = new RecordingLogSink();
        using var f = new WineBackendFixture(log);
        var target = await f.TargetAsync();

        await f.RemoveAsync(target);

        Assert.True(log.Logged($"Using Wine prefix {f.Prefix.Root}"));
    }

    // Fix round 1, finding 1: every terminal UnlockerResult.Fail(...) site now goes through the
    // one `Failed` helper, which logs at Error before returning. The log ends at "Step 7/10:
    // Copying version.dll" and just stops otherwise, on the elevated subsystem a user can least
    // diagnose without help. One test per site, asserting both the text and LogSeverity.Error.

    [Fact]
    public async Task A_fetch_failure_during_install_is_logged_as_an_error()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        ((StubAssetSource)f.Assets).Throw =
            new UnauthorizedAccessException("Access to the path is denied.");

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.True(log.Logged("Access to the path is denied", LogSeverity.Error));
    }

    [Fact]
    public async Task Surviving_client_processes_during_install_are_logged_as_an_error()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        f.Host.Running.Add("EADesktop");
        f.Host.Survivors.Add("EADesktop");
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.True(log.Logged("still running and hold the client directory open", LogSeverity.Error));
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_configuration_folder_that_cannot_be_created_is_logged_as_an_error()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        // Pre-created by the fixture; ConfigDirectory itself does not exist yet, so locking this
        // parent stops step 4's own CreateDirectory rather than something earlier.
        var parent = Directory.GetParent(f.Paths.ConfigDirectory)!.FullName;

        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(parent);
        File.SetUnixFileMode(parent, locked);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(parent, unlocked);
        }

        Assert.False(result.Success);
        Assert.True(log.Logged("could not be created", LogSeverity.Error));
    }

    // Distinct from the "could not be created" site: here the directory already exists (so step 4
    // no-ops) and writing the files inside it is what fails.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_configuration_that_cannot_be_written_is_logged_as_an_error()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        Directory.CreateDirectory(f.Paths.ConfigDirectory);

        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(f.Paths.ConfigDirectory);
        File.SetUnixFileMode(f.Paths.ConfigDirectory, locked);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(f.Paths.ConfigDirectory, unlocked);
        }

        Assert.False(result.Success);
        Assert.True(log.Logged("configuration could not be written", LogSeverity.Error));
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_dll_that_cannot_be_written_during_install_is_logged_as_an_error()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();

        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(target.ClientPath);
        File.SetUnixFileMode(target.ClientPath, locked);
        UnlockerResult result;
        try
        {
            result = await f.InstallAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(target.ClientPath, unlocked);
        }

        Assert.False(result.Success);
        Assert.True(log.Logged("version.dll could not be written to", LogSeverity.Error));
    }

    [Fact]
    public async Task Surviving_client_processes_during_removal_are_logged_as_an_error()
    {
        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        f.Host.Running.Add("EADesktop");
        f.Host.Survivors.Add("EADesktop");

        var result = await f.RemoveAsync(target);

        Assert.False(result.Success);
        Assert.True(log.Logged("still running and hold the client directory open", LogSeverity.Error));
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_dll_that_cannot_be_removed_during_removal_is_logged_as_an_error()
    {
        if (OperatingSystem.IsWindows()) return;

        var log = new RecordingLogSink();
        using var f = new InstallFixture(log);
        var target = await f.TargetAsync();
        Assert.True((await f.InstallAsync(target)).Success);

        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(target.ClientPath);
        File.SetUnixFileMode(target.ClientPath, locked);
        UnlockerResult result;
        try
        {
            result = await f.RemoveAsync(target);
        }
        finally
        {
            File.SetUnixFileMode(target.ClientPath, unlocked);
        }

        Assert.False(result.Success);
        Assert.True(log.Logged("version.dll could not be removed from", LogSeverity.Error));
    }
}
