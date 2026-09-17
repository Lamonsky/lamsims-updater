using System.Collections.Generic;
using System.IO;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class OrphanCleanerTests
{
    /// <summary>
    /// A suffix added to <see cref="OrphanCleaner"/>'s chain must not start unlinking live locks:
    /// on Unix that removes the name while the holder keeps its handle, ending exclusion.
    /// </summary>
    [Fact]
    public void Leaves_lock_files_alone()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.LockFile("EP99"), "");

        new OrphanCleaner(paths).CleanOrphans(new HashSet<string>());

        Assert.True(File.Exists(paths.LockFile("EP99")));
    }

    [Fact]
    public void Deletes_partials_whose_code_is_unknown()
    {
        using var temp = new TempDir();
        temp.Write("EP99.part", "x");
        temp.Write("EP99.part.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Equal(2, deleted.Count);
        Assert.False(File.Exists(temp.File("EP99.part")));
        Assert.False(File.Exists(temp.File("EP99.part.json")));
    }

    [Fact]
    public void Keeps_partials_whose_code_is_known()
    {
        using var temp = new TempDir();
        temp.Write("EP01.part", "x");
        temp.Write("EP01.part.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Empty(deleted);
        Assert.True(File.Exists(temp.File("EP01.part")));
    }

    [Fact]
    public void Never_deletes_quarantined_archives_or_completed_archives()
    {
        using var temp = new TempDir();
        temp.Write("EP99.zip.bad", "x");
        temp.Write("EP99.zip", "x");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string>());

        Assert.Empty(deleted);
        Assert.True(File.Exists(temp.File("EP99.zip.bad")));
        Assert.True(File.Exists(temp.File("EP99.zip")));
    }

    [Fact]
    public void Missing_root_is_not_an_error()
    {
        using var temp = new TempDir();
        var cleaner = new OrphanCleaner(new DownloadPaths(Path.Combine(temp.Path, "absent")));

        Assert.Empty(cleaner.CleanOrphans(new HashSet<string>()));
    }

    [Fact]
    public void Deletes_a_digest_record_whose_code_is_unknown()
    {
        using var temp = new TempDir();
        temp.Write("EP99.zip.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Equal(temp.File("EP99.zip.json"), Assert.Single(deleted));
    }

    [Fact]
    public void Deletes_a_digest_record_whose_archive_is_gone()
    {
        // A record describing an archive that no longer exists vouches for nothing, and the
        // next download writes a fresh one.
        using var temp = new TempDir();
        temp.Write("EP01.zip.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Equal(temp.File("EP01.zip.json"), Assert.Single(deleted));
    }

    [Fact]
    public void Keeps_a_digest_record_that_still_describes_an_archive()
    {
        using var temp = new TempDir();
        temp.Write("EP01.zip", "x");
        temp.Write("EP01.zip.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Empty(deleted);
        Assert.True(File.Exists(temp.File("EP01.zip.json")));
    }

    [Fact]
    public void Never_deletes_a_quarantined_archive_that_still_has_a_record()
    {
        // The archive is gone once it is quarantined, so its record goes; the quarantined file
        // itself is evidence and only the user removes it.
        using var temp = new TempDir();
        temp.Write("EP01.zip.bad", "x");
        temp.Write("EP01.zip.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.True(File.Exists(temp.File("EP01.zip.bad")));
        Assert.False(File.Exists(temp.File("EP01.zip.json")));
    }

    [Fact]
    public void Ignores_a_file_that_is_nothing_but_a_suffix()
    {
        using var temp = new TempDir();
        temp.Write(".zip.json", "{}");
        temp.Write(".part", "x");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        // A blank code cannot name an archive, and asking DownloadPaths for one throws.
        Assert.Empty(cleaner.CleanOrphans(new HashSet<string> { "EP01" }));
    }

    /// <summary>
    /// An unlocker asset temp names no pack, so the code-keyed arms never see it.
    /// StaticUnlockerAssetSource deletes its own on the paths it can observe, but its TryDelete
    /// swallows a refused delete, so a temp also survives an unwritable root — not only a kill.
    /// Nothing else in the tree collects them, and they are up to the pinned DLL size each.
    /// </summary>
    [Fact]
    public void CleanAssetTemps_deletes_a_temp_a_dead_fetch_left_behind()
    {
        using var temp = new TempDir();
        temp.Write("ea_app_version.dll.a1b2c3d4.incoming", "half a dll");

        var deleted = new OrphanCleaner(new DownloadPaths(temp.Path)).CleanAssetTemps();

        Assert.Equal([temp.File("ea_app_version.dll.a1b2c3d4.incoming")], deleted);
        Assert.False(File.Exists(temp.File("ea_app_version.dll.a1b2c3d4.incoming")));
    }

    /// <summary>
    /// The two sweeps are separate because they are safe at different moments, and this is the
    /// assertion that keeps them separate. CleanOrphans runs off any catalog that loads, which a
    /// user can trigger mid-session with an unlocker fetch already in flight; a temp carries no
    /// code, so no set of known codes protects it, and deleting a live one fails that install.
    /// </summary>
    [Fact]
    public void CleanOrphans_leaves_an_asset_temp_alone()
    {
        using var temp = new TempDir();
        temp.Write("ea_app_version.dll.a1b2c3d4.incoming", "being written right now");

        var deleted = new OrphanCleaner(new DownloadPaths(temp.Path))
            .CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Empty(deleted);
        Assert.True(File.Exists(temp.File("ea_app_version.dll.a1b2c3d4.incoming")));
    }

    /// <summary>
    /// The temp sweep takes the temps and nothing else: the cached asset is what a temp BECOMES,
    /// and a partial download belongs to the other sweep, which alone knows the live codes.
    /// </summary>
    [Fact]
    public void CleanAssetTemps_leaves_the_cached_asset_and_the_partials_alone()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.UnlockerAssetFile("ea_app_version.dll"), "the real one");
        temp.Write("EP99.part", "x");

        Assert.Empty(new OrphanCleaner(paths).CleanAssetTemps());

        Assert.True(File.Exists(paths.UnlockerAssetFile("ea_app_version.dll")));
        Assert.True(File.Exists(temp.File("EP99.part")));
    }
}
