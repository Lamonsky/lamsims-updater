using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Text;
using LamSims.Core;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class AtomicFileTests
{
    // AtomicFile's text overload goes through a StreamWriter, which would re-encode a PE image.
    // 0x80 is not valid UTF-8, so it is the byte that catches such a round trip.
    [Fact]
    public async Task WriteAllBytesAsync_round_trips_bytes_no_text_writer_could_carry()
    {
        using var dir = new TempDir();
        var path = dir.File("payload.bin");
        byte[] bytes = [0x4D, 0x5A, 0x00, 0x80, 0xFF, 0xFE, 0x0D, 0x0A, 0x00];

        await AtomicFile.WriteAllBytesAsync(path, bytes, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WriteAllBytesAsync_replaces_an_existing_file_and_leaves_no_temp_behind()
    {
        using var dir = new TempDir();
        var path = dir.File("payload.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        await AtomicFile.WriteAllBytesAsync(path, new byte[] { 9 }, CancellationToken.None);

        Assert.Equal([9], await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    // Both assertions are needed: a copy also leaves the bytes at the destination, and it is the
    // copy that leaks a 240 KB orphan on every asset fetch.
    [Fact]
    public void MoveIntoPlace_moves_rather_than_copies()
    {
        using var dir = new TempDir();
        var temp = dir.File("incoming.tmp");
        var dest = dir.File("final.dll");
        File.WriteAllBytes(temp, [7, 7, 7]);

        AtomicFile.MoveIntoPlace(temp, dest);

        Assert.Equal([7, 7, 7], File.ReadAllBytes(dest));
        Assert.False(File.Exists(temp), "the temp file was copied, not moved");
    }

    [Fact]
    public void MoveIntoPlace_overwrites_an_existing_destination()
    {
        using var dir = new TempDir();
        var temp = dir.File("incoming.tmp");
        var dest = dir.File("final.dll");
        File.WriteAllBytes(temp, [2]);
        File.WriteAllBytes(dest, [1]);

        AtomicFile.MoveIntoPlace(temp, dest);

        Assert.Equal([2], File.ReadAllBytes(dest));
        Assert.False(File.Exists(temp), "the temp file was copied, not moved");
    }

    [Fact]
    public async Task WriteAllBytesAsync_cleans_up_temp_file_if_move_fails()
    {
        using var dir = new TempDir();
        var path = dir.File("payload.bin");
        byte[] bytes = [1, 2, 3];

        // Make MoveIntoPlace fail by passing a directory path as destination
        Directory.CreateDirectory(path);

        // Renaming onto a directory raises IOException on Unix and UnauthorizedAccessException on
        // Windows, and UnauthorizedAccessException does not derive from IOException. The type is
        // not what this test is about: that it threw at all, and that the temp file went with it.
        var thrown = await Record.ExceptionAsync(() =>
            AtomicFile.WriteAllBytesAsync(path, bytes, CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.True(thrown is IOException or UnauthorizedAccessException,
                    $"expected a move failure, got {thrown.GetType().Name}: {thrown.Message}");

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void UnlockerAssetFile_sits_under_root_and_keeps_the_extension()
    {
        var paths = new DownloadPaths("/downloads");

        Assert.Equal(Path.Combine("/downloads", "ea_app_version.dll"),
                     paths.UnlockerAssetFile("ea_app_version.dll"));
    }

    // Every path under Root goes through here because OrphanCleaner sweeps Root; a name that
    // escaped it would send that sweep outside.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape.dll")]
    [InlineData("..")]
    [InlineData(".")]
    public void UnlockerAssetFile_rejects_a_name_that_could_escape_root(string name)
    {
        var paths = new DownloadPaths("/downloads");

        Assert.Throws<ArgumentException>(() => paths.UnlockerAssetFile(name));
    }
}
