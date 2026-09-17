using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class WineDllOverrideTests
{
    /// <summary>The empty block every real prefix already carries after wineboot -i.</summary>
    private const string EmptyBlock =
        "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Wine\\\\DllOverrides] 1787506419\n"
        + "#time=1dd332588fcb704\n";

    private sealed class Fixture : IDisposable
    {
        public PrefixFixture Prefix { get; } = new();
        public AppPaths App { get; }
        public WineOverrideStore Store { get; }

        public Fixture()
        {
            Prefix.WriteUserReg(EmptyBlock);
            App = new AppPaths(Path.Combine(Prefix.Dir.Path, "app"));
            App.EnsureCreated();
            Store = new WineOverrideStore(App);
        }

        public WinePrefix Open() => Prefix.Open();

        public string UserReg => Path.Combine(Prefix.Root, "user.reg");

        public void Dispose() => Prefix.Dispose();
    }

    [Fact]
    public async Task An_absent_override_is_written_and_recorded()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        var error = await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                                    CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);

        var record = f.Store.Read(prefix.Root);
        Assert.NotNull(record);
        Assert.True(record.WroteRegistry);
        Assert.Null(record.PriorValue);
        Assert.False(record.CreatedBlock);
    }

    /// <summary>
    /// Every launcher config already supplying the override means nothing has to be written, so the
    /// install can proceed with the prefix live. The record says so, and removal then touches
    /// nothing: without SkippedBecause, a later removal would clear an override this application
    /// never wrote.
    /// </summary>
    [Fact]
    public async Task A_prefix_whose_launchers_supply_the_override_is_left_alone()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        Assert.Null(await WineDllOverride.ApplyAsync(prefix, f.Store,
                                                    OverrideVerdict.SuppliesNative,
                                                    CancellationToken.None));

        Assert.Equal(EmptyBlock, await File.ReadAllTextAsync(f.UserReg));

        var record = f.Store.Read(prefix.Root);
        Assert.NotNull(record);
        Assert.False(record.WroteRegistry);
        Assert.NotNull(record.SkippedBecause);
    }

    /// <summary>
    /// Hostile still writes: the registry entry helps every other launch path, and refusing would
    /// leave a user whose third game config forces the builtin with no unlocker at all.
    /// </summary>
    [Fact]
    public async Task A_hostile_launcher_override_still_writes_the_registry()
    {
        using var f = new Fixture();

        Assert.Null(await WineDllOverride.ApplyAsync(f.Open(), f.Store,
                                                    OverrideVerdict.ForcesBuiltin,
                                                    CancellationToken.None));

        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);
    }

    /// <summary>
    /// The other half of the flush problem, and the whole reason liveness detection is allowed to
    /// be approximate: wineserver flushes on a timer and lingers after its last client, so a write
    /// can be discarded
    /// AFTER it succeeded. Verified by re-reading, not predicted.
    /// </summary>
    [Fact]
    public async Task A_write_that_does_not_survive_is_reported_as_a_failure()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        // Stands in for the flush: the file goes back to its pre-write content the moment after the
        // write lands, exactly as a lingering wineserver's save does.
        var error = await WineDllOverride.ApplyAsync(
            prefix, f.Store, OverrideVerdict.Absent, CancellationToken.None,
            afterWrite: () => File.WriteAllText(f.UserReg, EmptyBlock));

        Assert.NotNull(error);
        Assert.Contains("did not survive", error);

        // And nothing is recorded, so a later removal does not try to undo a write that was lost.
        Assert.Null(f.Store.Read(prefix.Root));
    }

    /// <summary>
    /// Install never overwrites an existing record. Otherwise the ordinary reinstall records our own
    /// "native,builtin" as PriorValue, and removal then "restores" the override — so the unlocker
    /// keeps loading after the user was told it was gone.
    /// </summary>
    [Fact]
    public async Task A_second_install_does_not_overwrite_the_first_records_prior_value()
    {
        using var f = new Fixture();
        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"*version\"=\"builtin\"\n"));
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);
        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        Assert.Equal("builtin", f.Store.Read(prefix.Root)?.PriorValue);
    }

    /// <summary>
    /// A pre-existing value that does NOT satisfy the requirement is replaced, recorded, and put
    /// back verbatim. The prior value has to be "builtin" and not "native": anything Classify reads
    /// as native makes IsSatisfied true, so ApplyAsync takes the skip path and the file is unchanged
    /// for a completely different reason — a round-trip assertion that would pass against a
    /// PriorValue mechanism that does not work at all.
    /// </summary>
    [Fact]
    public async Task A_prior_value_is_written_back_verbatim()
    {
        using var f = new Fixture();
        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"*version\"=\"builtin\"\n"));
        var before = await File.ReadAllTextAsync(f.UserReg);
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        // It really was replaced, so the restore below has work to do.
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);
        Assert.Equal("builtin", f.Store.Read(prefix.Root)?.PriorValue);

        Assert.Null(await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(f.UserReg));
        Assert.Null(f.Store.Read(prefix.Root));
    }

    /// <summary>
    /// A record can outlive the "*version" entry it describes: the launcher override that made
    /// ApplyAsync take the skip path can be removed, or the entry itself hand-edited out, while the
    /// record stays. An install that saw the record and returned immediately would report
    /// "Installed" while nothing loaded the DLL, and reinstalling could never repair it.
    /// The repair must also keep the record's ORIGINAL PriorValue — not one derived from the repair
    /// write, which by then has no prior value of its own to report — so removal still restores
    /// what the user actually had.
    /// </summary>
    [Fact]
    public async Task A_missing_entry_is_repaired_and_the_original_prior_value_survives()
    {
        using var f = new Fixture();
        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"*version\"=\"builtin\"\n"));
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);
        Assert.Equal("builtin", f.Store.Read(prefix.Root)?.PriorValue);

        // The entry vanishes; the record does not.
        await WineRegistryFile.RemoveValueAsync(prefix.UserRegFile, WineDllOverride.Key,
                                                WineDllOverride.ValueName, removeBlockIfEmpty: false,
                                                CancellationToken.None);
        Assert.Null(WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version"));

        var error = await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                                    CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);

        // The ORIGINAL prior value, not something the repair write itself reported.
        Assert.Equal("builtin", f.Store.Read(prefix.Root)?.PriorValue);
    }

    /// <summary>
    /// The most reachable path to the same defect: a launcher supplying "native" needs no registry
    /// write at all, so the record is WroteRegistry=false/SkippedBecause="already in place" with no
    /// value on disk to protect. If the user later removes that launcher override, the prefix is
    /// left with no override and a stale record; an ApplyAsync that saw the record and returned
    /// immediately would leave the unlocker inert on every subsequent install.
    /// </summary>
    [Fact]
    public async Task Losing_the_launcher_supplied_override_is_repaired_on_the_next_install()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.SuppliesNative,
                                        CancellationToken.None);
        var skipped = f.Store.Read(prefix.Root);
        Assert.False(skipped?.WroteRegistry);
        Assert.Null(WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version"));

        // The launcher no longer supplies it, and nothing in the registry does either.
        var error = await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                                    CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);
        Assert.True(f.Store.Read(prefix.Root)?.WroteRegistry);
    }

    /// <summary>
    /// A user's own "*version"="native,builtin" already in place survives
    /// removal. It takes the SKIP path rather than the PriorValue path — the requirement is already
    /// met, so nothing is written and nothing is undone — and that distinction is why both this and
    /// the test above exist.
    /// </summary>
    [Fact]
    public async Task A_users_own_satisfying_override_survives_a_round_trip()
    {
        using var f = new Fixture();
        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"*version\"=\"native,builtin\"\n"));
        var before = await File.ReadAllTextAsync(f.UserReg);
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);
        Assert.False(f.Store.Read(prefix.Root)?.WroteRegistry);

        Assert.Null(await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(f.UserReg));
    }

    [Fact]
    public async Task With_no_prior_value_the_line_is_removed_and_the_file_returns_to_its_original()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);
        await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None);

        Assert.Equal(EmptyBlock, await File.ReadAllTextAsync(f.UserReg));
    }

    [Fact]
    public async Task A_block_this_application_created_is_removed_with_the_value()
    {
        using var f = new Fixture();
        const string original = "WINE REGISTRY Version 2\n#arch=win64\n";
        f.Prefix.WriteUserReg(original);
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);
        Assert.True(f.Store.Read(prefix.Root)?.CreatedBlock);

        await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllTextAsync(f.UserReg));
    }

    /// <summary>
    /// Removal branches on the RECORD, never on what any launcher config now says. A user who adds a
    /// Lutris override after installing must not be left with this application's registry write in
    /// place for ever — which is the outcome the whole three-valued predicate exists to prevent.
    /// </summary>
    [Fact]
    public async Task Removal_undoes_a_recorded_write_whatever_the_launchers_now_say()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);
        await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None);

        Assert.Null(WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version"));
    }

    [Fact]
    public async Task Removal_with_a_skipped_record_touches_nothing()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.SuppliesNative,
                                        CancellationToken.None);
        Assert.Null(await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None));

        Assert.Equal(EmptyBlock, await File.ReadAllTextAsync(f.UserReg));
        Assert.Null(f.Store.Read(prefix.Root));
    }

    [Fact]
    public async Task Removal_with_no_record_at_all_touches_nothing()
    {
        using var f = new Fixture();
        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"*version\"=\"native\"\n"));
        var before = await File.ReadAllTextAsync(f.UserReg);

        Assert.Null(await WineDllOverride.UndoAsync(f.Open(), f.Store, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(f.UserReg));
    }

    [Fact]
    public void IsSatisfied_is_true_only_for_a_starred_entry_that_prefers_native()
    {
        using var f = new Fixture();

        Assert.False(WineDllOverride.IsSatisfied(f.Open()));

        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"version\"=\"n,b\"\n"));
        Assert.False(WineDllOverride.IsSatisfied(f.Open()));

        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"*version\"=\"n,b\"\n"));
        Assert.True(WineDllOverride.IsSatisfied(f.Open()));

        f.Prefix.WriteUserReg(EmptyBlock.Replace("#time=1dd332588fcb704\n",
                                                 "#time=1dd332588fcb704\n\"*version\"=\"b,n\"\n"));
        Assert.False(WineDllOverride.IsSatisfied(f.Open()));
    }

    /// <summary>
    /// A record outlives its prefix: AppPaths holds it, so a Proton recreate, a deleted bottle or a
    /// removed prefix leaves a record claiming there is work to do, and the undo would write a stale
    /// value into a freshly generated user.reg.
    /// </summary>
    [Fact]
    public async Task The_sweep_deletes_and_reports_a_record_whose_prefix_is_gone()
    {
        using var f = new Fixture();
        var prefix = f.Open();
        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        var vanished = Path.Combine(f.Prefix.Dir.Path, "was-a-prefix");
        await f.Store.WriteAsync(new WineOverrideRecord(vanished, true, null, false, null),
                                 CancellationToken.None);

        var reported = WineDllOverride.SweepOrphans(f.Store, "playday");

        Assert.Single(reported);
        Assert.Contains(vanished, reported[0]);
        Assert.Null(f.Store.Read(vanished));

        // The live prefix's record is untouched: a sweep that cleared everything would make every
        // removal a no-op.
        Assert.NotNull(f.Store.Read(prefix.Root));
    }

    /// <summary>
    /// The other reason a prefix path stops existing: its volume is not mounted. A Proton prefix
    /// under a second Steam library on a removable or network drive is an ordinary place to keep
    /// one, and the sweep runs on every detection. Deleting the record then loses the only record
    /// of what the override replaced, and once the volume returns the override is still in the
    /// registry with nothing able to undo it. A deleted prefix leaves its parent directory behind;
    /// an absent mount does not, which is what tells the two apart. Paired with the test above, so
    /// neither "always delete" nor "never delete" passes.
    /// </summary>
    [Fact]
    public async Task The_sweep_keeps_a_record_whose_whole_path_is_unreachable()
    {
        using var f = new Fixture();
        var prefix = f.Open();
        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        // Neither the prefix nor its parent exists, the shape an unmounted volume leaves behind.
        var unreachable = Path.Combine(f.Prefix.Dir.Path, "not-mounted", "compatdata", "42", "pfx");
        await f.Store.WriteAsync(new WineOverrideRecord(unreachable, true, "builtin", false, null),
                                 CancellationToken.None);

        var reported = WineDllOverride.SweepOrphans(f.Store, "playday");

        Assert.Single(reported);
        Assert.Contains(unreachable, reported[0]);

        // Reported, but kept — with its PriorValue, which is the part that cannot be reconstructed.
        var kept = f.Store.Read(unreachable);
        Assert.NotNull(kept);
        Assert.Equal("builtin", kept.PriorValue);
    }

    [Fact]
    public async Task Removal_on_a_vanished_prefix_writes_nothing_and_drops_the_record()
    {
        using var f = new Fixture();
        var prefix = f.Open();
        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        Directory.Delete(prefix.Root, recursive: true);

        Assert.Null(await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None));
        Assert.Null(f.Store.Read(prefix.Root));
    }

    /// <summary>
    /// A hashed key is not proof. The record is planted in THIS prefix's own file while naming a
    /// different prefix — the shape a key collision produces — so a store that trusted the filename
    /// would hand it back and the undo would run against the wrong prefix. Writing it under the
    /// other prefix's key instead would prove nothing: that file is not the one being read.
    /// </summary>
    [Fact]
    public async Task A_record_naming_a_different_prefix_reads_as_absent()
    {
        using var f = new Fixture();
        var elsewhere = Path.Combine(f.Prefix.Dir.Path, "elsewhere");

        // Written for this prefix, then its contents replaced with a record naming another. The
        // file therefore sits at exactly the path Read(f.Prefix.Root) looks in.
        await f.Store.WriteAsync(new WineOverrideRecord(f.Prefix.Root, true, null, false, null),
                                 CancellationToken.None);
        var file = Directory.GetFiles(f.App.WineOverrideDirectory, "*.json").Single();
        await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(
            new WineOverrideRecord(elsewhere, true, null, false, null)));

        Assert.Null(f.Store.Read(f.Prefix.Root));

        // And the sweep still sees it, so a planted or stale record is cleaned up rather than
        // becoming invisible and permanent.
        Assert.Single(f.Store.All());
    }

    /// <summary>
    /// When the verification read itself fails (I/O, permission), we cannot tell if the write
    /// succeeded or was lost. Record it so removal can undo, but report the distinct error so the
    /// user knows the override's status is uncertain. Mode-gated: the only way to make the
    /// verification read fail is a real POSIX mode, and without the mode the read succeeds and
    /// ApplyAsync returns null.
    /// </summary>
    [PosixDenialFact]
    public async Task A_write_that_cannot_be_verified_is_recorded_and_reported()
    {
        if (OperatingSystem.IsWindows()) return;

        using var f = new Fixture();
        var prefix = f.Open();

        // Make the file unreadable during verification. The write succeeds, the afterWrite seam
        // makes verification fail, but the write really landed on disk.
        try
        {
            var error = await WineDllOverride.ApplyAsync(
                prefix, f.Store, OverrideVerdict.Absent, CancellationToken.None,
                afterWrite: () =>
                {
                    // Repeated inside the lambda because the platform analyser cannot carry the
                    // method's early return across the delegate boundary.
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(f.UserReg, UnixFileMode.None);
                });

            // An error came back, but it is the "could not be confirmed" error, not "did not survive".
            Assert.NotNull(error);
            Assert.Contains("could not be confirmed", error);

            // The write was recorded despite the read failure, so removal can undo it.
            var record = f.Store.Read(prefix.Root);
            Assert.NotNull(record);
            Assert.True(record.WroteRegistry);
        }
        finally
        {
            // Restore write access so the fixture's temporary directory can be deleted.
            File.SetUnixFileMode(f.UserReg, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                           UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    [Fact]
    public async Task The_sweep_deletes_a_record_whose_prefix_root_is_gone()
    {
        using var f = new Fixture();
        var prefix = f.Open();
        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        var vanished = Path.Combine(f.Prefix.Dir.Path, "was-a-prefix");
        Directory.CreateDirectory(vanished);
        await f.Store.WriteAsync(new WineOverrideRecord(vanished, true, null, false, null),
                                 CancellationToken.None);

        // Delete the root directory entirely.
        Directory.Delete(vanished, recursive: true);

        var reported = WineDllOverride.SweepOrphans(f.Store, "playday");

        Assert.Single(reported);
        Assert.Contains(vanished, reported[0]);
        Assert.Contains("no longer exists", reported[0]);
        Assert.Null(f.Store.Read(vanished));

        // The live prefix's record survives.
        Assert.NotNull(f.Store.Read(prefix.Root));
    }

    /// <summary>
    /// A record whose prefix root still exists but is no longer a valid Wine prefix is reported
    /// and kept. This discriminates "damaged prefix" from "vanished prefix": the damaged one
    /// should not be silently deleted, and a subsequent removal should report the problem rather
    /// than succeeding on an absent file.
    /// </summary>
    [Fact]
    public async Task The_sweep_reports_but_keeps_a_record_whose_prefix_root_exists_but_is_invalid()
    {
        using var f = new Fixture();
        var prefix = f.Open();
        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        var damaged = Path.Combine(f.Prefix.Dir.Path, "damaged-prefix");
        Directory.CreateDirectory(damaged);
        await f.Store.WriteAsync(new WineOverrideRecord(damaged, true, null, false, null),
                                 CancellationToken.None);

        // The root exists but has no user.reg, so TryOpen will fail validation.

        var reported = WineDllOverride.SweepOrphans(f.Store, "playday");

        // One report: the damaged prefix (the live one is valid so not reported).
        Assert.Single(reported);
        Assert.Contains(damaged, reported[0]);
        Assert.Contains("not a valid", reported[0]);

        // The record is kept, not deleted.
        Assert.NotNull(f.Store.Read(damaged));

        // The live prefix's record also survives.
        Assert.NotNull(f.Store.Read(prefix.Root));
    }

    /// <summary>
    /// UndoAsync on a damaged prefix (root exists but user.reg missing) should return an error
    /// without attempting to write. The record is kept because the issue cannot be automatically
    /// resolved, and manual recovery might be needed.
    /// </summary>
    [Fact]
    public async Task Removal_on_a_damaged_prefix_keeps_the_record_and_writes_nothing()
    {
        using var f = new Fixture();
        var prefix = f.Open();
        await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                        CancellationToken.None);

        // Delete the user.reg file, leaving the root directory.
        File.Delete(f.UserReg);

        // Try to remove: should fail and keep the record.
        var error = await WineDllOverride.UndoAsync(prefix, f.Store, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("does not have a valid user.reg", error);

        // The record is kept, not deleted.
        Assert.NotNull(f.Store.Read(prefix.Root));

        // And no new user.reg was fabricated.
        Assert.False(File.Exists(f.UserReg));
    }

    /// <summary>
    /// The registry write lands and is confirmed, but the record that makes it reversible cannot be
    /// saved — a full or read-only configuration volume. That combination is the only one nothing
    /// can recover from: the user's previous "*version" value has been replaced, and with no record
    /// a later removal reads nothing, does nothing and reports success. So it has to be reported,
    /// not swallowed. Both halves are asserted, because a run that had refused to write at all
    /// would also produce an error. Mode-gated: the write is made to fail with a POSIX mode.
    /// </summary>
    [PosixDenialFact]
    public async Task An_override_whose_record_cannot_be_saved_is_reported_not_swallowed()
    {
        if (OperatingSystem.IsWindows()) return;

        using var f = new Fixture();
        var prefix = f.Open();

        // Give the prefix a value of its own, so there is something a lost record would strand.
        await WineRegistryFile.SetValueAsync(f.UserReg, WineDllOverride.Key, "*version", "builtin",
                                             CancellationToken.None);

        Directory.CreateDirectory(f.App.WineOverrideDirectory);
        var unlocked = File.GetUnixFileMode(f.App.WineOverrideDirectory);
        File.SetUnixFileMode(f.App.WineOverrideDirectory,
                             UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                                        CancellationToken.None);

            Assert.NotNull(error);
            Assert.Contains("record", error);

            // The override really was set, which is what makes the missing record consequential
            // rather than merely untidy.
            Assert.Equal("native,builtin",
                         WineRegistryFile.ReadValue(f.UserReg, WineDllOverride.Key, "*version")?.Text);
        }
        finally
        {
            File.SetUnixFileMode(f.App.WineOverrideDirectory, unlocked);
        }
    }

    /// <summary>
    /// The install side of the same damaged prefix. WinePrefix validates on system.reg and drive_c,
    /// so a prefix with no user.reg opens successfully, and WineRegistryFile reads a missing file as
    /// an empty one — so the write would create a user.reg holding only our own block, with no
    /// "WINE REGISTRY Version 2" header. Wine rejects such a hive, and the confirming re-read parses
    /// our own file and finds the value, so the run would report success while the override never
    /// loads. Both halves are asserted: the error, and that nothing was created.
    /// </summary>
    [Fact]
    public async Task An_install_on_a_damaged_prefix_writes_nothing_and_reports_it()
    {
        using var f = new Fixture();
        var prefix = f.Open();

        File.Delete(f.UserReg);

        var error = await WineDllOverride.ApplyAsync(prefix, f.Store, OverrideVerdict.Absent,
                                                    CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("does not have a valid user.reg", error);

        // No user.reg was fabricated, and nothing was recorded for a removal to undo.
        Assert.False(File.Exists(f.UserReg));
        Assert.Null(f.Store.Read(prefix.Root));
    }
}
