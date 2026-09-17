using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using System.Runtime.Versioning;
using LamSims.Core.Downloading;
using LamSims.Core.Queueing;

namespace LamSims.Core.Tests;

public class PackLockTests
{
    // PackLock leans on FileShare excluding a second open inside one process; without that it
    // would need an in-process registry as well as the file.
    [Fact]
    public void Two_opens_of_one_file_with_no_sharing_conflict_within_a_single_process()
    {
        using var temp = new TempDir();
        var path = temp.File("probe.lock");

        using var first = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    [Fact]
    public void The_lock_file_survives_release_so_the_path_always_names_one_inode()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        PackLock.TryAcquire(paths, "EP01")!.Dispose();

        // Not deleted on release. If it were, an acquirer that opened the path just before a
        // release could hold a flock on an unnamed inode while the next acquirer creates a fresh
        // inode at the same path, leaving two holders.
        Assert.True(File.Exists(paths.LockFile("EP01")));
    }

    [Fact]
    public void A_second_acquire_is_refused_while_the_first_is_held_and_allowed_after()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        var first = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(first);
        Assert.Null(PackLock.TryAcquire(paths, "EP01"));

        first!.Dispose();
        using var third = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(third);
    }

    [Fact]
    public void Different_codes_do_not_contend()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        using var a = PackLock.TryAcquire(paths, "EP01");
        using var b = PackLock.TryAcquire(paths, "EP02");

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_root_that_cannot_be_written_throws_rather_than_reporting_contention()
    {
        // ReadOnlyDir locks the directory via File.SetUnixFileMode, which does not exist on Windows.
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        using var locked = new ReadOnlyDir(temp.Path);
        var paths = new DownloadPaths(locked.Child);

        // ThrowsAny<Exception> would also pass on an ArgumentException from DownloadPaths.Validate.
        Assert.ThrowsAny<DirectoryNotFoundException>(() => PackLock.TryAcquire(paths, "EP01"));
    }

    /// <summary>
    /// The child is this same test assembly, re-entered through <see cref="LockChildHook"/>.
    ///
    /// Linux only, and that is a coverage gap rather than a platform claim: PackLock takes
    /// FileShare.None, which Windows enforces more strictly than Unix, so the behaviour under test
    /// is if anything better there. What does not work on the Windows CI leg is the harness — the
    /// child is started from Environment.ProcessPath, which under `dotnet test` is VSTest's test
    /// host rather than a runtime that will re-enter this assembly, and the readiness marker never
    /// appears. Fixing that needs a Windows machine to iterate on, not a guess from here.
    /// </summary>
    [LinuxFact("Linux only: the child-process harness does not re-enter this assembly under the "
               + "Windows test host. The lock itself is not platform-specific.")]
    public async Task A_lock_held_by_another_process_blocks_this_one()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        var ready = temp.File("child-ready");
        var release = temp.File("child-release");
        // Fresh per run, so an exported environment or a marker left by some other process
        // cannot stand in for a child that this test actually started.
        var token = Guid.NewGuid().ToString("N");

        var host = Environment.ProcessPath!;
        var info = new System.Diagnostics.ProcessStartInfo(host)
        {
            UseShellExecute = false,
        };
        // The child re-enters this same assembly through the module initializer.
        info.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        info.Environment["LAMSIMS_LOCK_CHILD_TOKEN"] = token;
        info.Environment["LAMSIMS_LOCK_CHILD_ROOT"] = temp.Path;
        info.Environment["LAMSIMS_LOCK_CHILD_READY"] = ready;
        info.Environment["LAMSIMS_LOCK_CHILD_RELEASE"] = release;

        using var child = System.Diagnostics.Process.Start(info)!;

        try
        {
            // No sleeping on a fixed interval: poll for the child's readiness marker with a
            // bounded number of yields, and fail loudly if it never appears.
            for (var i = 0; i < 600 && !File.Exists(ready); i++)
                await Task.Delay(50);

            Assert.True(File.Exists(ready), "the child process never acquired the lock");
            Assert.Equal(token, File.ReadAllText(ready));
            Assert.Null(PackLock.TryAcquire(paths, "EP01"));
        }
        finally
        {
            File.WriteAllText(release, "go");
            if (!child.WaitForExit(30_000))
            {
                child.Kill(entireProcessTree: true);
                // Wait for the kill to land: the acquire below must not race a file descriptor
                // that is still open, or it reports a second failure on top of the real one.
                child.WaitForExit(30_000);
            }
        }

        using var afterChildExited = PackLock.TryAcquire(paths, "EP01");
        Assert.NotNull(afterChildExited);
    }
}
