using System;
using System.IO;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

// Shares SpecialFolderRoots with AppPathsTests, which mutates the XDG variables this class reads
// through GetFolderPath. Every class that builds a paths object on the real environment belongs to
// that collection, which is what stops them running beside the mutation.
[Collection(SpecialFolderRoots.Name)]
public class DownloadPathsTests
{
    [Fact]
    public void Root_defaults_under_local_application_data()
    {
        var paths = new DownloadPaths();

        // Rooted is asserted separately: the suffix below is drawn from the same call the class
        // makes, so it agrees with a relative Root as readily as with an absolute one.
        Assert.True(Path.IsPathRooted(paths.Root), $"'{paths.Root}' is not an absolute path");

        var expected = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "lamsims-updater", "downloads");

        Assert.Equal(expected, paths.Root);
    }

    [Fact]
    public void Root_honours_the_override()
    {
        using var temp = new TempDir();

        var paths = new DownloadPaths(temp.Path);

        Assert.Equal(temp.Path, paths.Root);
    }

    [Fact]
    public void File_names_follow_the_code()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "EP01.part"), paths.PartFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.part.json"), paths.StateFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.zip"), paths.ArchiveFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.zip.bad"), paths.QuarantineFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.zip.json"), paths.ArchiveDigestFile("EP01"));
    }

    [Fact]
    public void Lock_file_sits_beside_the_archive_and_rejects_a_path_character()
    {
        var paths = new DownloadPaths("/tmp/lamsims-x");

        Assert.Equal(Path.Combine("/tmp/lamsims-x", "EP01.lock"), paths.LockFile("EP01"));
        Assert.Throws<ArgumentException>(() => paths.LockFile("../EP01"));
    }

    [Fact]
    public void EnsureCreated_creates_the_root()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "nested", "downloads");
        var paths = new DownloadPaths(root);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void Blank_code_is_rejected()
    {
        var paths = new DownloadPaths();

        Assert.Throws<ArgumentException>(() => paths.PartFile("  "));
    }

    [Fact]
    public void Retargeting_moves_every_path_it_hands_out_to_the_new_root()
    {
        // Every consumer holds this one instance and asks it for paths per call, so moving Root
        // is what lets the download directory change without rebuilding the object graph.
        using var temp = new TempDir();
        var moved = Path.Combine(temp.Path, "elsewhere");

        var paths = new DownloadPaths(temp.File("first"));
        paths.Retarget(moved);

        Assert.Equal(moved, paths.Root);
        Assert.Equal(Path.Combine(moved, "EP01.part"), paths.PartFile("EP01"));
        Assert.Equal(Path.Combine(moved, "EP01.part.json"), paths.StateFile("EP01"));
        Assert.Equal(Path.Combine(moved, "EP01.zip"), paths.ArchiveFile("EP01"));
        Assert.Equal(Path.Combine(moved, "EP01.zip.bad"), paths.QuarantineFile("EP01"));
        Assert.Equal(Path.Combine(moved, "EP01.zip.json"), paths.ArchiveDigestFile("EP01"));
        Assert.Equal(Path.Combine(moved, "EP01.lock"), paths.LockFile("EP01"));
        Assert.Equal(Path.Combine(moved, "dll.dll"), paths.UnlockerAssetFile("dll.dll"));
    }

    [Fact]
    public void Retargeting_creates_the_directory_it_moves_to()
    {
        // PackLock does not create the root and throws when it is missing, so a retarget that
        // only recorded the new path would break the first pack queued after the change.
        using var temp = new TempDir();
        var moved = Path.Combine(temp.Path, "made", "by", "retarget");

        new DownloadPaths(temp.Path).Retarget(moved);

        Assert.True(Directory.Exists(moved));
    }

    [Fact]
    public void A_retarget_that_cannot_create_the_directory_leaves_the_old_root_in_use()
    {
        // A user picking a directory on an unmounted drive gets the exception bannered by the
        // caller. If the paths moved anyway the engine would point at a directory that is gone.
        using var temp = new TempDir();
        var blocked = temp.File("a-file-not-a-directory");
        File.WriteAllText(blocked, "");

        var paths = new DownloadPaths(temp.Path);

        Assert.ThrowsAny<IOException>(() => paths.Retarget(Path.Combine(blocked, "downloads")));
        Assert.Equal(temp.Path, paths.Root);
    }

    [Fact]
    public void Clearing_the_setting_returns_to_the_root_the_paths_were_built_with()
    {
        // Null is how the user says "wherever you would have put them". It must resolve to the
        // constructed default rather than to the previous override, or clearing the setting
        // would silently leave downloads in the directory the user just cleared.
        using var temp = new TempDir();
        var original = temp.File("default");

        var paths = new DownloadPaths(original);
        paths.Retarget(temp.File("chosen"));
        paths.Retarget(null);

        Assert.Equal(original, paths.Root);
        Assert.Equal(original, paths.DefaultRoot);
    }
}
