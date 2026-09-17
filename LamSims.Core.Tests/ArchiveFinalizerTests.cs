using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Text;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class ArchiveFinalizerTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static async Task<(DownloadPaths Paths, PartStateStore State)> Prepare(TempDir temp, string content)
    {
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.PartFile("EP01"), Encoding.ASCII.GetBytes(content));
        var state = new PartStateStore(paths.StateFile("EP01"));
        await state.SaveAsync(
            new PartState("EP01", content.Length, AbcSha256, 100,
                new[] { new CompletedChunk(0, "https://a.example/EP01.zip") }, Array.Empty<MirrorValidator>()),
            CancellationToken.None);
        return (paths, state);
    }

    [Fact]
    public async Task A_matching_digest_promotes_the_part_file_to_an_archive()
    {
        using var temp = new TempDir();
        var (paths, state) = await Prepare(temp, "abc");

        var result = await new ArchiveFinalizer(paths)
            .FinalizeAsync("EP01", AbcSha256, state, usedSingleStream: false, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(paths.ArchiveFile("EP01"), result.FilePath);
        Assert.True(File.Exists(paths.ArchiveFile("EP01")));
        Assert.False(File.Exists(paths.PartFile("EP01")));
        Assert.False(File.Exists(paths.StateFile("EP01")));
    }

    [Fact]
    public async Task An_existing_archive_from_a_failed_install_is_replaced()
    {
        using var temp = new TempDir();
        var (paths, state) = await Prepare(temp, "abc");
        await File.WriteAllTextAsync(paths.ArchiveFile("EP01"), "stale");

        var result = await new ArchiveFinalizer(paths)
            .FinalizeAsync("EP01", AbcSha256, state, usedSingleStream: false, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal("abc", await File.ReadAllTextAsync(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task A_mismatched_digest_quarantines_the_file_and_reports_both_digests()
    {
        using var temp = new TempDir();
        var (paths, state) = await Prepare(temp, "abc");
        const string wrongExpected = "0000000000000000000000000000000000000000000000000000000000000000";

        var result = await new ArchiveFinalizer(paths)
            .FinalizeAsync("EP01", wrongExpected, state, usedSingleStream: false, CancellationToken.None);

        Assert.Equal(DownloadOutcome.ChecksumMismatch, result.Outcome);
        Assert.Equal(wrongExpected, result.ExpectedSha256);
        Assert.Equal(AbcSha256, result.ActualSha256);
        Assert.Equal(paths.QuarantineFile("EP01"), result.FilePath);
        Assert.True(File.Exists(paths.QuarantineFile("EP01")));
        Assert.False(File.Exists(paths.PartFile("EP01")));
        Assert.False(File.Exists(paths.StateFile("EP01")));
    }

    [Fact]
    public async Task A_missing_part_file_fails_rather_than_throwing()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        var result = await new ArchiveFinalizer(paths).FinalizeAsync(
            "EP01", AbcSha256, new PartStateStore(paths.StateFile("EP01")),
            usedSingleStream: false, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("EP01.part", result.Error);
    }

    [Fact]
    public async Task A_missing_part_file_also_discards_the_stale_sidecar()
    {
        // Resume state describing a file that no longer exists would make the next run
        // skip chunks in a freshly zero-filled part file.
        using var temp = new TempDir();
        var (paths, state) = await Prepare(temp, "abc");
        File.Delete(paths.PartFile("EP01"));

        var result = await new ArchiveFinalizer(paths)
            .FinalizeAsync("EP01", AbcSha256, state, usedSingleStream: false, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(paths.StateFile("EP01")));
    }

    [Fact]
    public async Task A_matching_digest_also_records_the_archive_it_promoted()
    {
        // A later run installs this archive off the record instead of re-hashing it.
        using var temp = new TempDir();
        var (paths, state) = await Prepare(temp, "abc");

        await new ArchiveFinalizer(paths)
            .FinalizeAsync("EP01", AbcSha256, state, usedSingleStream: false, CancellationToken.None);

        var record = new ArchiveDigestStore(paths).TryLoad("EP01");
        var info = new FileInfo(paths.ArchiveFile("EP01"));

        Assert.NotNull(record);
        Assert.Equal(AbcSha256, record.Sha256);
        Assert.Equal(3, record.Length);

        // Statted after the rename, so the identity recorded is what any later run will see.
        Assert.Equal(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), record.LastWriteTimeUtc);
    }

    [Fact]
    public async Task A_mismatched_digest_records_nothing()
    {
        using var temp = new TempDir();
        var (paths, state) = await Prepare(temp, "abc");

        await new ArchiveFinalizer(paths).FinalizeAsync(
            "EP01", "0000000000000000000000000000000000000000000000000000000000000000",
            state, usedSingleStream: false, CancellationToken.None);

        Assert.False(File.Exists(paths.ArchiveDigestFile("EP01")));
    }
}
