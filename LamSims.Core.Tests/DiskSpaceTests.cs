using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class DiskSpaceTests
{
    [Fact]
    public void Reports_positive_free_space_for_an_existing_directory()
    {
        using var temp = new TempDir();

        Assert.True(DiskSpace.GetAvailableBytes(temp.Path) > 0);
    }

    [Fact]
    public void Walks_up_to_the_nearest_existing_ancestor()
    {
        using var temp = new TempDir();
        var missing = Path.Combine(temp.Path, "does", "not", "exist");

        Assert.True(DiskSpace.GetAvailableBytes(missing) > 0);
    }

    [Fact]
    public void Measures_the_mount_holding_the_path_not_the_os_root()
    {
        // Unix-only by construction: "/" is the OS root here, and DriveInfo rejects it on
        // Windows, where the equivalent question is answered by
        // PlatformSemanticsTests.DriveInfo_normalises_a_directory_to_its_volume_root.
        if (OperatingSystem.IsWindows()) return;

        // Path.GetPathRoot returns "/" for every absolute path on Unix, so measuring the
        // path root would silently report the OS filesystem for a downloads directory on
        // any other mount. Skipped on machines with only one mount, where the two agree.
        var otherMount = DriveInfo.GetDrives()
            .FirstOrDefault(d => d.Name != "/" && d.IsReady
                                 && d.AvailableFreeSpace != new DriveInfo("/").AvailableFreeSpace);
        if (otherMount is null) return;

        Assert.Equal(otherMount.AvailableFreeSpace, DiskSpace.GetAvailableBytes(otherMount.Name));
        Assert.NotEqual(DiskSpace.GetAvailableBytes("/"), DiskSpace.GetAvailableBytes(otherMount.Name));
    }

    [Fact]
    public void EnsureAvailable_passes_when_space_suffices()
    {
        using var temp = new TempDir();

        DiskSpace.EnsureAvailable(temp.Path, 1024);
    }

    [Fact]
    public void EnsureAvailable_throws_when_space_is_short()
    {
        using var temp = new TempDir();

        var ex = Assert.Throws<InsufficientDiskSpaceException>(
            () => DiskSpace.EnsureAvailable(temp.Path, long.MaxValue));

        Assert.Equal(long.MaxValue, ex.RequiredBytes);
        Assert.True(ex.AvailableBytes >= 0);
    }
}
