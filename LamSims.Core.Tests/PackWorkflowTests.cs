using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using LamSims.Core;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Logging;

namespace LamSims.Core.Tests;

public class PackWorkflowTests
{
    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static PackEntry Pack(byte[] archive, Uri url) => new(
        "EP01", "The Sims 4 Get to Work", PackType.Expansion, archive.LongLength, null,
        Sha256Of(archive), new[] { url }, new[] { "EP01" });

    private static byte[] BuildArchive(TempDir temp)
    {
        var path = ZipBuilder.Create(temp.File("source.zip"), ("EP01/a.package", "content"));
        return File.ReadAllBytes(path);
    }

    /// <summary>The Sims 4 install directory always pre-exists; the installer requires it to.</summary>
    private static string GameDir(TempDir temp)
    {
        var path = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(path);
        return path;
    }

    private static PackWorkflow Workflow(
        TempDir temp, HttpClient client, out DownloadPaths paths, ILogSink? log = null)
    {
        paths = new DownloadPaths(Path.Combine(temp.Path, "downloads"));
        var options = new DownloadOptions { Connections = 2 };

        return new PackWorkflow(
            new SegmentedDownloader(client, paths, options, RetryOptions.Default, new FakeDelayProvider()),
            new ZipInstaller(new InstallStateStore(Path.Combine(temp.Path, "installs"))),
            paths, log);
    }

    /// <summary>
    /// An archive already on disk, correct and recorded as verified. Shared by the phase tests
    /// that start from a pre-existing archive. The digest record and the download URL are both
    /// real but unexercised unless a test knocks the record aside.
    /// </summary>
    private static async Task<(PackWorkflow Workflow, PackEntry Pack, string GameDir, DownloadPaths Paths, byte[] ArchiveBytes)>
        BuildWorkflowWithArchiveOnDiskAsync(TempDir temp, HttpClient client, ILogSink? log = null)
    {
        var archive = BuildArchive(temp);
        var workflow = Workflow(temp, client, out var paths, log);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var pack = Pack(archive, new Uri("http://127.0.0.1/unused.zip"));
        await new ArchiveDigestStore(paths).RecordAsync(pack.Code, paths.ArchiveFile(pack.Code), Sha256Of(archive));

        return (workflow, pack, GameDir(temp), paths, archive);
    }

    [Fact]
    public async Task Reports_downloading_then_installing_when_no_archive_is_on_disk()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out _);
        var phases = new List<PackPhase>();

        await workflow.RunAsync(
            Pack(archive, server.FileUrl), GameDir(temp),
            new SyncProgress<PackPhase>(phases.Add), null, null, CancellationToken.None);

        Assert.Equal(new[] { PackPhase.Downloading, PackPhase.Installing }, phases);
    }

    [Fact]
    public async Task Reports_verifying_when_an_archive_is_on_disk_without_a_digest_record()
    {
        using var temp = new TempDir();
        using var client = new HttpClient();
        var phases = new List<PackPhase>();
        var log = new RecordingLogSink();

        var (workflow, pack, gameDir, paths, _) = await BuildWorkflowWithArchiveOnDiskAsync(temp, client, log);
        // Archive present and correct, but no <code>.zip.json beside it.
        File.Delete(paths.ArchiveDigestFile(pack.Code));

        await workflow.RunAsync(
            pack, gameDir, new SyncProgress<PackPhase>(phases.Add), null, null, CancellationToken.None);

        Assert.Equal(new[] { PackPhase.Verifying, PackPhase.Installing }, phases);
        Assert.True(log.Logged("Verifying the archive", LogSeverity.Info));
    }

    [Fact]
    public async Task Reports_installing_only_when_the_digest_record_vouches_for_the_archive()
    {
        using var temp = new TempDir();
        using var client = new HttpClient();
        var phases = new List<PackPhase>();
        var log = new RecordingLogSink();

        var (workflow, pack, gameDir, _, _) = await BuildWorkflowWithArchiveOnDiskAsync(temp, client, log);
        // BuildWorkflowWithArchiveOnDiskAsync leaves a matching record in place.

        await workflow.RunAsync(
            pack, gameDir, new SyncProgress<PackPhase>(phases.Add), null, null, CancellationToken.None);

        Assert.Equal(new[] { PackPhase.Installing }, phases);
        Assert.True(log.Logged(
            "Trusting the existing archive, its digest record still matches", LogSeverity.Info));
    }

    [Fact]
    public async Task Downloads_then_installs_a_pack()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        var game = GameDir(temp);

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), game, null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.Equal(DownloadOutcome.Completed, result.Download!.Outcome);
        Assert.Equal(InstallOutcome.Installed, result.Install!.Outcome);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "a.package")));
        Assert.False(File.Exists(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task Stops_at_the_download_when_the_checksum_does_not_match()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out _);
        var pack = Pack(archive, server.FileUrl) with
        {
            Sha256 = new string('a', 64),
        };

        var result = await workflow.RunAsync(
            pack, GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Downloading, result.ReachedStage);
        Assert.Equal(DownloadOutcome.ChecksumMismatch, result.Download!.Outcome);
        Assert.Null(result.Install);
    }

    [Fact]
    public async Task Installs_an_archive_that_is_already_verified_without_downloading_again()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var game = GameDir(temp);

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), game, null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.Null(result.Download);

        // No record accompanies this archive, so the trust ladder re-hashes it rather than
        // fetching several gigabytes again. Reading at disk speed beats a fresh transfer.
        Assert.Equal(0, server.RequestCount);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "a.package")));
    }

    [Fact]
    public async Task Downloads_again_when_an_existing_archive_is_the_wrong_length()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive[..^1]);

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl),
            GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.NotNull(result.Download);
        Assert.True(server.RequestCount > 0);
    }

    [Fact]
    public async Task Stops_at_the_install_and_keeps_the_archive_when_installing_fails()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var pack = Pack(archive, server.FileUrl) with { InstalledSize = long.MaxValue };

        var result = await workflow.RunAsync(
            pack, GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Installing, result.ReachedStage);
        Assert.Equal(InstallOutcome.InsufficientSpace, result.Install!.Outcome);
        Assert.True(File.Exists(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task Cancelling_during_the_install_phase_reports_it_and_keeps_the_verified_archive()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("source.zip"),
            ("EP01/a.package", "one"),
            ("EP01/b.package", "two"));
        var bytes = File.ReadAllBytes(archive);

        await using var server = await TestFileServer.StartAsync(bytes);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), bytes);

        using var cancellation = new CancellationTokenSource();
        var installProgress = new SyncProgress<InstallProgress>(_ => cancellation.Cancel());

        var result = await workflow.RunAsync(
            Pack(bytes, server.FileUrl), GameDir(temp),
            null, null, installProgress, cancellation.Token);

        Assert.Equal(PackStage.Installing, result.ReachedStage);
        Assert.Equal(InstallOutcome.Cancelled, result.Install!.Outcome);

        // The archive is already verified; a cancelled install must not force a re-download.
        Assert.True(File.Exists(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task Downloads_again_when_an_existing_archive_is_the_right_length_but_the_wrong_bytes()
    {
        // Any file of the catalogued length sitting under the pack's name must not be extracted
        // into the game directory unhashed.
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();
        var log = new RecordingLogSink();

        var workflow = Workflow(temp, client, out var paths, log);
        paths.EnsureCreated();

        var impostor = new byte[archive.Length];
        Array.Fill(impostor, (byte)0);
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), impostor);

        var game = GameDir(temp);
        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), game, null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.True(server.RequestCount > 0);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "a.package")));

        // Quarantined rather than overwritten: the user's file is not deleted because we
        // disagree about its hash, and the mismatch leaves evidence.
        Assert.True(File.Exists(paths.QuarantineFile("EP01")));
        Assert.True(log.Logged("Archive quarantined:", LogSeverity.Warning));
    }

    [Fact]
    public async Task Warns_when_an_existing_archive_is_quarantined()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();

        var impostor = new byte[archive.Length];
        Array.Fill(impostor, (byte)0);
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), impostor);

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), GameDir(temp), null, null, null, CancellationToken.None);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("EP01", warning);
        Assert.Contains(paths.QuarantineFile("EP01"), warning);
    }

    [Fact]
    public async Task Re_hashes_an_unrecorded_archive_rather_than_downloading_it_again()
    {
        // Migration on the archive side: a .zip left by a pre-journal build has no record. It
        // costs one sequential read, once, and the record it writes makes every later run cheap.
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        // InstalledSize stops the install before the archive is deleted, so the record written
        // during the re-hash is still there to assert on.
        var pack = Pack(archive, server.FileUrl) with { InstalledSize = long.MaxValue };

        var result = await workflow.RunAsync(pack, GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Installing, result.ReachedStage);
        Assert.Equal(0, server.RequestCount);

        var record = new ArchiveDigestStore(paths).TryLoad("EP01");
        var info = new FileInfo(paths.ArchiveFile("EP01"));

        Assert.NotNull(record);
        Assert.Equal(pack.Sha256, record.Sha256);
        Assert.Equal(info.Length, record.Length);
        Assert.Equal(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), record.LastWriteTimeUtc);
    }

    [Fact]
    public async Task Re_hashes_an_archive_whose_record_does_not_describe_it()
    {
        // The record is bound to a file snapshot, not to a name: a same-length file swapped in
        // under a stale record has to be re-hashed, or the swap inherits the verification.
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var pack = Pack(archive, server.FileUrl) with { InstalledSize = long.MaxValue };
        var digests = new ArchiveDigestStore(paths);

        await digests.SaveAsync(
            new ArchiveDigest(ArchiveDigest.CurrentSchemaVersion, "EP01", pack.Sha256, archive.LongLength,
                new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        await workflow.RunAsync(pack, GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(0, server.RequestCount);

        // Refreshed from a stat of the real file, so the stale timestamp is gone.
        var info = new FileInfo(paths.ArchiveFile("EP01"));
        Assert.Equal(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), digests.TryLoad("EP01")!.LastWriteTimeUtc);
    }

    [Fact]
    public async Task Re_hashes_an_archive_whose_record_names_a_digest_the_catalog_no_longer_has()
    {
        // The stale-catalog case: the record is otherwise accurate about the file on disk, but
        // the catalog has since moved the pack to a different digest, so the record must not be
        // trusted on its say-so alone.
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var pack = Pack(archive, server.FileUrl) with { InstalledSize = long.MaxValue };
        var digests = new ArchiveDigestStore(paths);
        var info = new FileInfo(paths.ArchiveFile("EP01"));

        await digests.SaveAsync(
            new ArchiveDigest(ArchiveDigest.CurrentSchemaVersion, "EP01", new string('a', 64),
                info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)),
            CancellationToken.None);

        await workflow.RunAsync(pack, GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(0, server.RequestCount);
        Assert.Equal(pack.Sha256, digests.TryLoad("EP01")!.Sha256);
    }

    [Fact]
    public async Task Installs_a_recorded_archive_without_hashing_it_again()
    {
        // The record's digest is seeded in uppercase. Rung 2 compares OrdinalIgnoreCase so it
        // still matches, while Sha256Verifier.ComputeAsync emits lowercase, so a re-hash would
        // normalise the sidecar. A timestamp cannot tell the two paths apart, since a re-hash
        // writes the same values back from the same unmodified file.
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var pack = Pack(archive, server.FileUrl) with { InstalledSize = long.MaxValue };
        var digests = new ArchiveDigestStore(paths);
        var info = new FileInfo(paths.ArchiveFile("EP01"));
        await digests.SaveAsync(
            new ArchiveDigest(ArchiveDigest.CurrentSchemaVersion, "EP01", pack.Sha256.ToUpperInvariant(),
                info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)),
            CancellationToken.None);

        var before = await File.ReadAllBytesAsync(paths.ArchiveDigestFile("EP01"));

        await workflow.RunAsync(pack, GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(0, server.RequestCount);
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.ArchiveDigestFile("EP01")));
    }

    [Fact]
    public async Task Deletes_the_digest_record_when_the_install_succeeds()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), GameDir(temp), null, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.False(File.Exists(paths.ArchiveFile("EP01")));
        Assert.False(File.Exists(paths.ArchiveDigestFile("EP01")));
    }

    [Fact]
    public async Task Reports_a_cancellation_during_the_re_hash_rather_than_throwing()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), GameDir(temp), null, null, null, cancellation.Token);

        Assert.Equal(PackStage.Downloading, result.ReachedStage);
        Assert.Equal(DownloadOutcome.Cancelled, result.Download!.Outcome);
        Assert.Null(result.Install);
        Assert.Equal(0, server.RequestCount);
    }

    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Reports_the_installers_journal_warning_through_the_workflow()
    {
        // Same technique as ZipInstallerTests.Warns_but_still_reports_installed_...: lock the
        // marker's group directory from inside the install progress callback so only the
        // completion journal write fails, and check the resulting warning reaches the
        // workflow's own result rather than being swallowed on the way up.
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out _);
        var installs = Path.Combine(temp.Path, "installs");
        var store = new InstallStateStore(installs);
        var game = GameDir(temp);

        var lockedMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var readableMode = lockedMode | UnixFileMode.UserWrite;
        var group = Path.GetDirectoryName(store.MarkerFile(game, "EP01"))!;

        var installProgress = new SyncProgress<InstallProgress>(_ => File.SetUnixFileMode(group, lockedMode));

        try
        {
            var result = await workflow.RunAsync(
                Pack(archive, server.FileUrl), game, null, null, installProgress, CancellationToken.None);

            Assert.Equal(PackStage.Done, result.ReachedStage);

            var warning = Assert.Single(result.Warnings);
            Assert.Contains("EP01", warning);
            Assert.Contains(installs, warning);
        }
        finally
        {
            // Restored so the enclosing TempDir can be deleted.
            if (Directory.Exists(group)) File.SetUnixFileMode(group, readableMode);
        }
    }
}
