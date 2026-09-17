using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class UnlockerInstallRecordTests
{
    private static (UnlockerInstallRecordStore Store, AppPaths Paths) Build(TempDir dir)
    {
        var paths = new AppPaths(Path.Combine(dir.Path, "app"));
        return (new UnlockerInstallRecordStore(paths), paths);
    }

    [Fact]
    public async Task A_written_record_reads_back()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);

        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, true), CancellationToken.None);

        var read = store.Read("/scope/one", ClientKind.EaApp);
        Assert.NotNull(read);
        Assert.Equal("/clients/ea", read.ClientPath);
        Assert.True(read.OwnsAutostartBackup);
    }

    // Two clients in one scope must not share a file. Asserting only that each reads back would
    // pass against a store keyed by scope alone, because the second write would overwrite the
    // first and both reads would return the second record.
    [Fact]
    public async Task Two_clients_in_one_scope_keep_separate_records()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);

        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, true), CancellationToken.None);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/origin", ClientKind.Origin, false), CancellationToken.None);

        Assert.Equal("/clients/ea", store.Read("/scope/one", ClientKind.EaApp)!.ClientPath);
        Assert.Equal("/clients/origin", store.Read("/scope/one", ClientKind.Origin)!.ClientPath);
    }

    [Fact]
    public async Task The_same_client_in_two_scopes_keeps_separate_records()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);

        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/a/ea", ClientKind.EaApp, false), CancellationToken.None);
        await store.WriteAsync("/scope/two",
            new UnlockerInstallRecord("/b/ea", ClientKind.EaApp, false), CancellationToken.None);

        Assert.Equal("/a/ea", store.Read("/scope/one", ClientKind.EaApp)!.ClientPath);
        Assert.Equal("/b/ea", store.Read("/scope/two", ClientKind.EaApp)!.ClientPath);
    }

    [Fact]
    public async Task Others_reports_the_scope_s_other_clients_and_excludes_the_asking_one()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);

        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, true), CancellationToken.None);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/origin", ClientKind.Origin, false), CancellationToken.None);
        await store.WriteAsync("/scope/two",
            new UnlockerInstallRecord("/elsewhere/ea", ClientKind.EaApp, false), CancellationToken.None);

        var others = store.Others("/scope/one", ClientKind.EaApp);

        Assert.True(others.Complete);
        Assert.Equal(ClientKind.Origin, Assert.Single(others.Records).Client);
    }

    // "Nobody else is installed" and "I cannot tell" must be different answers: the caller
    // deletes a shared directory on the strength of the first. A store that returned an empty
    // list for both would pass an Assert.Empty here and silently destroy the other client's
    // configuration in production.
    [Fact]
    public async Task A_corrupt_sibling_record_makes_the_answer_incomplete()
    {
        using var dir = new TempDir();
        var (store, paths) = Build(dir);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, false), CancellationToken.None);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/origin", ClientKind.Origin, false), CancellationToken.None);
        var sibling = Directory.GetFiles(paths.UnlockerInstallDirectory)
            .First(f => f.Contains("origin"));
        await File.WriteAllTextAsync(sibling, "{ not json");

        var others = store.Others("/scope/one", ClientKind.EaApp);

        Assert.False(others.Complete);
        Assert.Empty(others.Records);
    }

    // The outer catch in Others() covers a directory that cannot be enumerated at all, which is
    // a different failure from a directory that enumerates fine but holds a corrupt record (the
    // test above, which trips the inner "record is null" branch instead). Locking the directory
    // itself, rather than a file inside it, is the only way to reach that catch.
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_install_directory_makes_the_answer_incomplete()
    {
        // SetUnixFileMode is a no-op on Windows, where the directory would stay enumerable and
        // Others() would report Complete regardless of this test.
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var (store, paths) = Build(dir);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, false), CancellationToken.None);

        var unlocked = File.GetUnixFileMode(paths.UnlockerInstallDirectory);
        File.SetUnixFileMode(paths.UnlockerInstallDirectory, UnixFileMode.None);

        try
        {
            var others = store.Others("/scope/one", ClientKind.EaApp);

            Assert.False(others.Complete);
            Assert.Empty(others.Records);
        }
        finally
        {
            // Restore before the fixture disposes, or TempDir cannot delete the tree.
            File.SetUnixFileMode(paths.UnlockerInstallDirectory, unlocked);
        }
    }

    [Fact]
    public async Task A_deleted_record_reads_as_absent_and_leaves_its_sibling()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, false), CancellationToken.None);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/origin", ClientKind.Origin, false), CancellationToken.None);

        store.Delete("/scope/one", ClientKind.EaApp);

        Assert.Null(store.Read("/scope/one", ClientKind.EaApp));
        Assert.NotNull(store.Read("/scope/one", ClientKind.Origin));
    }

    // The key is a hash, so a record must still prove which client it describes. Without this a
    // key collision would let a record vouch for the wrong install, the same reasoning
    // InstallStateStore documents for its own grouped markers.
    [Fact]
    public async Task A_record_whose_client_path_disagrees_with_the_caller_is_discarded()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, false), CancellationToken.None);

        Assert.Null(store.Read("/scope/one", ClientKind.EaApp, expectedClientPath: "/clients/other"));
        Assert.NotNull(store.Read("/scope/one", ClientKind.EaApp, expectedClientPath: "/clients/ea"));
    }

    [Fact]
    public void Reading_an_absent_record_returns_null_rather_than_throwing()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);

        Assert.Null(store.Read("/scope/none", ClientKind.EaApp));
        // Absent is a complete answer of none, unlike the corrupt case above.
        var others = store.Others("/scope/none", ClientKind.EaApp);
        Assert.True(others.Complete);
        Assert.Empty(others.Records);
    }

    [Fact]
    public async Task A_corrupt_record_reads_as_absent_rather_than_throwing()
    {
        using var dir = new TempDir();
        var (store, paths) = Build(dir);
        await store.WriteAsync("/scope/one",
            new UnlockerInstallRecord("/clients/ea", ClientKind.EaApp, false), CancellationToken.None);
        var file = Directory.GetFiles(paths.UnlockerInstallDirectory)[0];
        await File.WriteAllTextAsync(file, "{ not json");

        Assert.Null(store.Read("/scope/one", ClientKind.EaApp));
    }
    /// <summary>
    /// Two scopes differing only in case are two scopes. Keyed with case folded they shared one
    /// file, so installing the second erased the first's record and the first's removal then had
    /// nothing to restore from. Both halves are read back, because asserting only the second
    /// would pass for a store that had thrown the first away.
    /// </summary>
    [Fact]
    public async Task Two_scopes_that_differ_only_in_case_keep_their_own_records()
    {
        using var dir = new TempDir();
        var (store, _) = Build(dir);

        await store.WriteAsync("/scope/Prefix",
            new UnlockerInstallRecord("/clients/upper", ClientKind.EaApp, true), CancellationToken.None);
        await store.WriteAsync("/scope/prefix",
            new UnlockerInstallRecord("/clients/lower", ClientKind.EaApp, true), CancellationToken.None);

        Assert.Equal("/clients/upper", store.Read("/scope/Prefix", ClientKind.EaApp)?.ClientPath);
        Assert.Equal("/clients/lower", store.Read("/scope/prefix", ClientKind.EaApp)?.ClientPath);
    }

}
