using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

/// <summary>
/// Platform facts the download and install paths are built on, asserted rather than assumed. Each
/// was taken from documentation, so what matters is what the windows-latest and macos-latest CI
/// legs report, not what a Linux developer machine says.
/// </summary>
public class PlatformSemanticsTests
{
    /// <summary>
    /// Why the download paths close a handle before deleting the file it referred to. Windows
    /// refuses the delete while a FileShare.Read handle is open; Unix unlinks happily. Both
    /// directions are asserted so neither platform passes by accident.
    /// </summary>
    [Fact]
    public void An_open_read_handle_blocks_delete_only_on_windows()
    {
        using var temp = new TempDir();
        var path = temp.File("held.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.Delete(path));
                Assert.True(File.Exists(path));
            }
            else
            {
                // Unix unlinks a file with a live handle: the name goes immediately, the inode
                // survives until the handle closes. This is why the download paths close before
                // deleting rather than relying on the delete to tell them anything.
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    /// <summary>
    /// Sha256Verifier opens its FileStream with FileOptions.SequentialScan and not
    /// FileOptions.Asynchronous, so the handle is synchronous. The awaits still yield on Linux,
    /// but if a synchronous handle's ReadAsync completes inline on Windows the whole hash runs
    /// before the method returns and holds the caller's thread for its duration.
    ///
    /// Asserted as "did it return before finishing" rather than as a duration, since a timing
    /// threshold on a shared runner flakes. The file is 16 MB against a 1 MB buffer, so sixteen
    /// thread-pool round trips would have to finish between the return and the check.
    /// </summary>
    [Fact]
    public async Task Hashing_a_file_yields_before_it_finishes()
    {
        using var temp = new TempDir();
        var path = temp.File("large.bin");
        await File.WriteAllBytesAsync(path, new byte[16 * 1024 * 1024]);

        var hashing = Sha256Verifier.ComputeAsync(path, CancellationToken.None);
        var returnedBeforeFinishing = !hashing.IsCompleted;

        await hashing;

        Assert.True(returnedBeforeFinishing,
            "ComputeAsync ran to completion inline, so awaiting it never releases the caller's thread.");
    }

    /// <summary>
    /// DiskSpace passes the directory itself to DriveInfo, never Path.GetPathRoot, because on Unix
    /// every absolute path roots at "/" and the root would report the OS filesystem instead of the
    /// mount holding the downloads. That relies on DriveInfo accepting a full path: on Windows the
    /// constructor normalises a path to its volume root and rejects a UNC path outright.
    ///
    /// The volume root is compared against a directory nested beneath it, since a temp directory
    /// compared against its own subdirectory sits on the same volume by construction and never
    /// exercises normalisation.
    ///
    /// The root comes from DriveInfo.GetDrives() rather than Path.GetPathRoot, which on Unix always
    /// returns "/". Where the system temp path is its own mount, as with tmpfs on /tmp, that would
    /// compare two genuinely different volumes and fail for the wrong reason.
    /// </summary>
    [Fact]
    public void DriveInfo_normalises_a_directory_to_its_volume_root()
    {
        using var temp = new TempDir();

        var deep = Path.Combine(temp.Path, "a", "b", "c");
        Directory.CreateDirectory(deep);

        var root = DriveInfo.GetDrives()
            .Select(drive => drive.RootDirectory.FullName)
            .Where(candidate => temp.Path.StartsWith(candidate, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length)
            .First();

        var atRoot = DiskSpace.GetAvailableBytes(root);
        var atDepth = DiskSpace.GetAvailableBytes(deep);

        // Both readings must name the same volume, not the same instant: free space is live, and two
        // reads on a busy runner can differ by a few blocks without the normalisation being wrong.
        // 64 MiB is far below any distinct-volume difference and far above incidental churn.
        Assert.True(atRoot > 0);
        Assert.True(atDepth > 0);
        Assert.True(Math.Abs(atRoot - atDepth) < 64L * 1024 * 1024,
            $"free space differed by {Math.Abs(atRoot - atDepth)} bytes, which suggests different volumes");
    }

    /// <summary>
    /// DriveInfo rejects a UNC path outright, having no volume root to normalise to. That matters
    /// because DiskSpace hands DriveInfo a directory path rather than a path root, so a UNC
    /// download directory must fail rather than silently measure another volume.
    ///
    /// Asserted against DriveInfo directly: DiskSpace.GetAvailableBytes throws
    /// DirectoryNotFoundException from its ancestor walk first, because Path.GetDirectoryName of a
    /// UNC share root is null, so a test routed through it would pass regardless. Going direct
    /// also attempts no name resolution for the nonexistent host.
    /// </summary>
    [Fact]
    public void DriveInfo_refuses_a_unc_path()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<ArgumentException>(() => new DriveInfo(@"\\server\share"));
    }
}
