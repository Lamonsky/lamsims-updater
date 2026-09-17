using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class ArchiveDigestStoreTests
{
    private const string Digest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static (DownloadPaths Paths, ArchiveDigestStore Store) Prepare(TempDir temp)
    {
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        return (paths, new ArchiveDigestStore(paths));
    }

    private static ArchiveDigest Record(long length = 3, DateTimeOffset? written = null) => new(
        ArchiveDigest.CurrentSchemaVersion, "EP01", Digest, length,
        written ?? new DateTimeOffset(2026, 8, 15, 13, 58, 1, 412, TimeSpan.Zero));

    [Fact]
    public async Task Round_trips_a_record()
    {
        using var temp = new TempDir();
        var (_, store) = Prepare(temp);
        var written = new DateTimeOffset(2026, 8, 15, 13, 58, 1, 412, TimeSpan.Zero).AddTicks(8841);

        await store.SaveAsync(Record(6871947673, written), CancellationToken.None);
        var loaded = store.TryLoad("EP01");

        Assert.NotNull(loaded);
        Assert.Equal(Digest, loaded.Sha256);
        Assert.Equal(6871947673, loaded.Length);

        // Exact ticks: this value is compared against a fresh stat, so anything the format
        // rounds away turns every run into a re-hash.
        Assert.Equal(written, loaded.LastWriteTimeUtc);
    }

    [Fact]
    public void An_absent_record_is_null_rather_than_an_exception()
    {
        using var temp = new TempDir();
        var (_, store) = Prepare(temp);

        Assert.Null(store.TryLoad("EP01"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP0")]
    [InlineData("\u0000\u0001 not json")]
    [InlineData("{\"schemaVersion\": 2, \"code\": \"EP01\", \"sha256\": \"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\", \"length\": 3, \"lastWriteTimeUtc\": \"2026-08-15T13:58:01Z\"}")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP01\", \"sha256\": \"nope\", \"length\": 3, \"lastWriteTimeUtc\": \"2026-08-15T13:58:01Z\"}")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP01\", \"sha256\": \"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\", \"lastWriteTimeUtc\": \"2026-08-15T13:58:01Z\"}")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP01\", \"sha256\": \"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\", \"length\": 3}")]
    public async Task A_record_that_does_not_parse_cleanly_reads_as_absent(string contents)
    {
        using var temp = new TempDir();
        var (paths, store) = Prepare(temp);

        await File.WriteAllTextAsync(paths.ArchiveDigestFile("EP01"), contents);

        Assert.Null(store.TryLoad("EP01"));
    }

    [Fact]
    public async Task A_record_naming_another_pack_does_not_vouch_for_this_one()
    {
        using var temp = new TempDir();
        var (paths, store) = Prepare(temp);

        await store.SaveAsync(Record(), CancellationToken.None);
        File.Move(paths.ArchiveDigestFile("EP01"), paths.ArchiveDigestFile("SP30"));

        Assert.Null(store.TryLoad("SP30"));
    }

    [Fact]
    public async Task Deletes_a_record()
    {
        using var temp = new TempDir();
        var (paths, store) = Prepare(temp);

        await store.SaveAsync(Record(), CancellationToken.None);
        store.Delete("EP01");

        Assert.False(File.Exists(paths.ArchiveDigestFile("EP01")));
    }

    [Fact]
    public void Deleting_a_record_that_is_not_there_is_not_an_error()
    {
        using var temp = new TempDir();
        var (_, store) = Prepare(temp);

        store.Delete("EP01");
    }

    [Fact]
    public async Task Describes_a_file_from_its_own_stat()
    {
        using var temp = new TempDir();
        var (paths, _) = Prepare(temp);
        var archive = paths.ArchiveFile("EP01");
        await File.WriteAllTextAsync(archive, "abc");

        var record = ArchiveDigestStore.Describe("EP01", archive, Digest);
        var info = new FileInfo(archive);

        Assert.NotNull(record);
        Assert.Equal(ArchiveDigest.CurrentSchemaVersion, record.SchemaVersion);
        Assert.Equal("EP01", record.Code);
        Assert.Equal(Digest, record.Sha256);
        Assert.Equal(3, record.Length);
        Assert.Equal(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), record.LastWriteTimeUtc);
    }

    [Fact]
    public void Describing_a_file_that_is_not_there_is_null_rather_than_an_exception()
    {
        using var temp = new TempDir();

        Assert.Null(ArchiveDigestStore.Describe("EP01", temp.File("absent.zip"), Digest));
    }
}
