using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Runtime.Versioning;
using LamSims.Core.Catalogs;
using LamSims.Core.Installing;
using LamSims.Core.Scanning;

namespace LamSims.Core.Tests;

public class InstallScannerTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly IReadOnlyDictionary<string, InstallMarker> NoMarkers =
        new Dictionary<string, InstallMarker>();

    private static PackEntry Pack(string code, params string[] installDirs) => new(
        code, "Pack " + code, PackType.Expansion, 1024, null, Digest,
        new[] { new Uri($"https://host-a.example.invalid/{code}.zip") },
        installDirs.Length == 0 ? new[] { code } : installDirs);

    private static readonly DateTimeOffset Installed = new(2026, 8, 15, 14, 3, 22, TimeSpan.Zero);

    private static IReadOnlyDictionary<string, InstallMarker> Markers(params InstallMarker[] markers)
    {
        var byCode = new Dictionary<string, InstallMarker>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in markers) byCode[marker.Code] = marker;
        return byCode;
    }

    private static InstallMarker Marker(
        string code, string gameDirectory, InstallMarkerStatus status = InstallMarkerStatus.Installed) =>
        new(InstallMarker.CurrentSchemaVersion, code, gameDirectory, Digest, status, Installed);

    [Fact]
    public void Reports_a_pack_whose_directory_is_present_with_no_marker_as_installed_but_unverified()
    {
        // Covers installs this tool did not perform, upstream or pre-journal. The first run after
        // an upgrade sees the whole library this way, so the state must not invite a redownload.
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }, NoMarkers).Packs);

        Assert.Equal(PackInstallState.InstalledUnverified, result.State);
        Assert.Empty(result.MissingDirs);
        Assert.Null(result.InstalledSha256);
        Assert.Null(result.InstalledUtc);
    }

    [Fact]
    public void Reports_a_pack_whose_directory_is_present_with_a_completed_marker_as_installed()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01") }, Markers(Marker("EP01", temp.Path))).Packs);

        Assert.Equal(PackInstallState.Installed, result.State);
        Assert.Equal(Digest, result.InstalledSha256);
        Assert.Equal(Installed, result.InstalledUtc);
    }

    [Fact]
    public void Reports_a_pack_whose_marker_is_still_installing_as_partial_with_nothing_missing()
    {
        // Cancel a one-directory pack after its first entry and the directory exists, so
        // directory presence alone would report it installed.
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01") },
                Markers(Marker("EP01", temp.Path, InstallMarkerStatus.Installing))).Packs);

        Assert.Equal(PackInstallState.Partial, result.State);
        Assert.Empty(result.MissingDirs);
        Assert.Null(result.InstalledSha256);
    }

    [Fact]
    public void Ignores_a_marker_recorded_against_a_different_game_directory()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01") },
                Markers(Marker("EP01", Path.Combine(temp.Path, "elsewhere")))).Packs);

        Assert.Equal(PackInstallState.InstalledUnverified, result.State);
    }

    [Fact]
    public void Applies_a_marker_whose_game_directory_differs_only_by_a_trailing_separator()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01") },
                Markers(Marker("EP01", temp.Path + Path.DirectorySeparatorChar))).Packs);

        Assert.Equal(PackInstallState.Installed, result.State);
    }

    [Fact]
    public void Ignores_a_marker_whose_game_directory_cannot_be_resolved()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01") }, Markers(Marker("EP01", "\0not a path"))).Packs);

        // Not an exception out of Scan: one side of this comparison is a string read off disk.
        Assert.Equal(PackInstallState.InstalledUnverified, result.State);
    }

    [Fact]
    public void Ignores_a_marker_for_a_pack_whose_directories_are_gone()
    {
        // Directories are ground truth for absence; a marker left over from a since-deleted
        // install must not claim the pack is there.
        using var temp = new TempDir();

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01") }, Markers(Marker("EP01", temp.Path))).Packs);

        Assert.Equal(PackInstallState.NotInstalled, result.State);
        Assert.Null(result.InstalledSha256);
    }

    [Fact]
    public void Ignores_a_marker_for_a_pack_missing_one_of_its_directories()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "SP30"));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("SP30", "SP30", "SP30_Delta") },
                Markers(Marker("SP30", temp.Path))).Packs);

        Assert.Equal(PackInstallState.Partial, result.State);
        Assert.Equal("SP30_Delta", Assert.Single(result.MissingDirs));
        Assert.Null(result.InstalledSha256);
    }

    [Fact]
    public void Matches_a_decomposed_directory_name_against_a_composed_catalog_name()
    {
        // A pack directory created on an HFS+/APFS volume comes back decomposed. Comparing the
        // two forms ordinally reports a successfully installed pack as absent, forever.
        using var temp = new TempDir();
        var composed = "Café";                // é
        var decomposed = "Café";             // e + combining acute
        Directory.CreateDirectory(Path.Combine(temp.Path, decomposed));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01", composed) }, NoMarkers).Packs);

        Assert.Equal(PackInstallState.InstalledUnverified, result.State);
        Assert.Empty(result.MissingDirs);
    }

    [Fact]
    public void Matches_a_composed_directory_name_against_a_decomposed_catalog_name()
    {
        using var temp = new TempDir();
        var composed = "Café";
        var decomposed = "Café";
        Directory.CreateDirectory(Path.Combine(temp.Path, composed));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("EP01", decomposed) }, NoMarkers).Packs);

        Assert.Equal(PackInstallState.InstalledUnverified, result.State);
    }

    [Fact]
    public void Does_not_throw_on_an_install_directory_name_that_is_not_valid_unicode()
    {
        // A name on a Linux filesystem is an arbitrary byte sequence, and a catalog is a file
        // the user can point anywhere, so a lone surrogate can reach Normalize from either side
        // of the comparison. It cannot be normalised, so it matches no directory on disk.
        using var temp = new TempDir();
        var lone = "bad" + '\uD800';

        var scan = InstallScanner.Scan(temp.Path, new[] { Pack("EP01", lone) }, NoMarkers);

        Assert.Equal(PackInstallState.NotInstalled, Assert.Single(scan.Packs).State);
    }

    [Fact]
    public void Reports_a_pack_with_genuinely_empty_install_dirs_as_not_installed()
    {
        // The catalog parser defaults an absent or empty 'installDirs' to [code], so this only
        // arrives another way. No directories is no evidence of an install, and must not read as
        // "nothing missing".
        using var temp = new TempDir();
        var pack = new PackEntry(
            "EP01", "Pack EP01", PackType.Expansion, 1024, null, Digest,
            new[] { new Uri("https://host-a.example.invalid/EP01.zip") }, Array.Empty<string>());

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { pack }, NoMarkers).Packs);

        Assert.Equal(PackInstallState.NotInstalled, result.State);
        Assert.Empty(result.MissingDirs);
    }

    [Fact]
    public void Reports_a_pack_with_no_directory_as_not_installed()
    {
        using var temp = new TempDir();

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }, NoMarkers).Packs);

        Assert.Equal(PackInstallState.NotInstalled, result.State);
        Assert.Equal("EP01", Assert.Single(result.MissingDirs));
    }

    [Fact]
    public void Reports_a_pack_missing_one_of_its_directories_as_partial()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "SP30"));

        var result = Assert.Single(InstallScanner
            .Scan(temp.Path, new[] { Pack("SP30", "SP30", "SP30_Delta") }, NoMarkers).Packs);

        // A subset means an interrupted install, which needs a reinstall rather than the skip
        // "installed" would earn it.
        Assert.Equal(PackInstallState.Partial, result.State);
        Assert.Equal("SP30_Delta", Assert.Single(result.MissingDirs));
    }

    [Fact]
    public void Compares_directory_names_and_not_the_whole_path()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "SP20", "The Sims 4");
        Directory.CreateDirectory(game);

        var result = Assert.Single(InstallScanner.Scan(game, new[] { Pack("SP20") }, NoMarkers).Packs);

        // Upstream's substring match against the full path called this installed.
        Assert.Equal(PackInstallState.NotInstalled, result.State);
    }

    [Fact]
    public void Ignores_files_that_share_a_pack_name()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "EP01"), "not a directory");

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }, NoMarkers).Packs);

        Assert.Equal(PackInstallState.NotInstalled, result.State);
    }

    [Fact]
    public void Matches_directory_names_case_insensitively()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "ep01"));

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }, NoMarkers).Packs);

        Assert.Equal(PackInstallState.InstalledUnverified, result.State);
    }

    [Fact]
    public void Reports_every_pack_as_not_installed_when_the_game_directory_is_absent()
    {
        using var temp = new TempDir();

        var scan = InstallScanner.Scan(
            Path.Combine(temp.Path, "absent"), new[] { Pack("EP01"), Pack("EP02") }, NoMarkers);

        Assert.Equal(2, scan.Packs.Count);
        Assert.All(scan.Packs, r => Assert.Equal(PackInstallState.NotInstalled, r.State));
        Assert.False(scan.GameDirectoryReadable);
    }

    [Fact]
    public void Reports_the_game_directory_as_readable_when_it_exists_and_is_empty()
    {
        using var temp = new TempDir();

        var scan = InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }, NoMarkers);

        // Empty and unreadable both leave every pack NotInstalled; only this flag tells them
        // apart.
        Assert.True(scan.GameDirectoryReadable);
        Assert.Equal(PackInstallState.NotInstalled, Assert.Single(scan.Packs).State);
    }

    [Fact]
    public void Keeps_the_order_of_the_catalog()
    {
        using var temp = new TempDir();

        var scan = InstallScanner.Scan(temp.Path, new[] { Pack("EP02"), Pack("EP01") }, NoMarkers);

        Assert.Equal(new[] { "EP02", "EP01" }, scan.Packs.Select(r => r.Code));
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public void Reports_every_pack_as_not_installed_when_the_game_directory_cannot_be_read()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var gameDir = Path.Combine(temp.Path, "game");

        // Created before the directory is locked down. A readable gameDir reports EP01 as
        // installed, which is what separates "unreadable" from "empty" under a user who can read
        // through the chmod anyway, such as root.
        Directory.CreateDirectory(Path.Combine(gameDir, "EP01"));

        var unreadableMode = UnixFileMode.UserWrite;
        var readableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        try
        {
            File.SetUnixFileMode(gameDir, unreadableMode);

            // Markers are supplied and still must not be consulted: state may never claim a
            // presence the scan could not confirm.
            var scan = InstallScanner.Scan(
                gameDir, new[] { Pack("EP01"), Pack("EP02") }, Markers(Marker("EP01", gameDir)));

            Assert.Equal(2, scan.Packs.Count);
            Assert.All(scan.Packs, r => Assert.Equal(PackInstallState.NotInstalled, r.State));
            Assert.False(scan.GameDirectoryReadable);
        }
        finally
        {
            File.SetUnixFileMode(gameDir, readableMode);
        }
    }

    [Fact]
    public void Reads_a_whole_legacy_library_as_installed_but_unverified()
    {
        // Users have packs installed by upstream or by pre-journal runs. Reporting that library
        // as Partial would offer to redownload hundreds of gigabytes.
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "SP30"));

        var store = new InstallStateStore(Path.Combine(temp.Path, "installs"));

        var scan = InstallScanner.Scan(
            temp.Path, new[] { Pack("EP01"), Pack("SP30") }, store.LoadAll(temp.Path));

        Assert.All(scan.Packs, r =>
        {
            Assert.Equal(PackInstallState.InstalledUnverified, r.State);
            Assert.NotEqual(PackInstallState.Partial, r.State);
            Assert.NotEqual(PackInstallState.NotInstalled, r.State);
            Assert.Empty(r.MissingDirs);
        });
    }

    [Fact]
    public async Task Reads_a_corrupt_marker_as_installed_but_unverified()
    {
        // Only a well-formed 'installing' marker may assert an interruption, so corruption
        // cannot accuse a healthy library.
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "EP01"));

        var store = new InstallStateStore(Path.Combine(temp.Path, "installs"));
        await store.SaveAsync(
            new InstallMarker(InstallMarker.CurrentSchemaVersion, "EP01", game, Digest,
                InstallMarkerStatus.Installing, Installed),
            CancellationToken.None);

        await File.WriteAllTextAsync(store.MarkerFile(game, "EP01"), "{\"schemaVersion\": 1, \"code\":");

        var result = Assert.Single(
            InstallScanner.Scan(game, new[] { Pack("EP01") }, store.LoadAll(game)).Packs);

        Assert.Equal(PackInstallState.InstalledUnverified, result.State);
    }
}
