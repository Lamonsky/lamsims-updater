using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class PartStateStoreTests
{
    private static PartState SampleState() => new(
        Code: "EP01",
        TotalSize: 1000,
        ExpectedSha256: "abc123",
        ChunkSize: 100,
        CompletedChunks: new[]
        {
            new CompletedChunk(0, "https://a.example/EP01.zip"),
            new CompletedChunk(2, "https://a.example/EP01.zip"),
            new CompletedChunk(5, "https://b.example/EP01.zip"),
        },
        Mirrors: new[]
        {
            new MirrorValidator("https://a.example/EP01.zip", "\"v1\"", null, 1000),
            new MirrorValidator("https://b.example/EP01.zip", null, "Wed, 21 Oct 2015 07:28:00 GMT", 1000),
        });

    [Fact]
    public async Task Round_trips_a_state()
    {
        using var temp = new TempDir();
        var store = new PartStateStore(temp.File("EP01.part.json"));

        await store.SaveAsync(SampleState(), CancellationToken.None);
        var loaded = store.TryLoad();

        Assert.Equal(SampleState(), loaded);
    }

    [Fact]
    public void Missing_file_loads_as_null()
    {
        using var temp = new TempDir();

        Assert.Null(new PartStateStore(temp.File("absent.part.json")).TryLoad());
    }

    [Fact]
    public void A_truncated_file_loads_as_null_rather_than_throwing()
    {
        using var temp = new TempDir();
        temp.Write("EP01.part.json", "{\"Code\":\"EP01\",\"TotalSi");

        Assert.Null(new PartStateStore(temp.File("EP01.part.json")).TryLoad());
    }

    [Fact]
    public void Garbage_loads_as_null()
    {
        using var temp = new TempDir();
        temp.Write("EP01.part.json", "not json at all");

        Assert.Null(new PartStateStore(temp.File("EP01.part.json")).TryLoad());
    }

    [Fact]
    public async Task Leaves_no_temporary_file_behind()
    {
        using var temp = new TempDir();
        var store = new PartStateStore(temp.File("EP01.part.json"));

        await store.SaveAsync(SampleState(), CancellationToken.None);

        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task Concurrent_saves_leave_a_readable_state()
    {
        using var temp = new TempDir();
        var store = new PartStateStore(temp.File("EP01.part.json"));

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            store.SaveAsync(
                SampleState() with { CompletedChunks = new[] { new CompletedChunk(i, "https://a.example/EP01.zip") } },
                CancellationToken.None)));

        Assert.NotNull(store.TryLoad());
    }

    [Fact]
    public async Task Delete_removes_the_file_and_is_safe_to_repeat()
    {
        using var temp = new TempDir();
        var store = new PartStateStore(temp.File("EP01.part.json"));
        await store.SaveAsync(SampleState(), CancellationToken.None);

        store.Delete();
        store.Delete();

        Assert.False(File.Exists(temp.File("EP01.part.json")));
    }

    [Fact]
    public void A_weak_etag_is_not_a_usable_validator()
    {
        var weak = new MirrorValidator("https://a.example/f.zip", "W/\"v1\"", null, 10);

        Assert.False(weak.HasValidator);
        Assert.Null(weak.IfRangeValue);
    }

    [Fact]
    public void A_mirror_with_no_validator_never_matches_even_itself()
    {
        // Matching URL and length prove nothing about content. Treating this as
        // "unchanged" would let resume trust chunks of a file replaced between sessions.
        var probed = new MirrorValidator("https://a.example/f.zip", null, null, 1000);
        var stored = new MirrorValidator("https://a.example/f.zip", null, null, 1000);

        Assert.False(stored.Matches(probed));
    }

    [Fact]
    public void A_mirror_with_an_unchanged_strong_etag_matches()
    {
        var stored = new MirrorValidator("https://a.example/f.zip", "\"v1\"", null, 1000);

        Assert.True(stored.Matches(new MirrorValidator("https://a.example/f.zip", "\"v1\"", null, 1000)));
        Assert.False(stored.Matches(new MirrorValidator("https://a.example/f.zip", "\"v2\"", null, 1000)));
    }

    [Fact]
    public void Last_modified_serves_as_the_validator_when_no_etag_is_offered()
    {
        var validator = new MirrorValidator("https://a.example/f.zip", null, "Wed, 21 Oct 2015 07:28:00 GMT", 10);

        Assert.True(validator.HasValidator);
        Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", validator.IfRangeValue);
    }


    [Fact]
    public async Task Delete_sweeps_a_temp_file_left_by_a_kill_mid_save()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "EP01.part.json");
        var store = new PartStateStore(path);

        await store.SaveAsync(
            new PartState("EP01", 100, new string('a', 64), 50,
                Array.Empty<CompletedChunk>(), Array.Empty<MirrorValidator>()),
            CancellationToken.None);

        // AtomicFile names each temp uniquely, so a process killed mid-save leaves one behind
        // that no later save overwrites. Without a sweep they accumulate for the life of the
        // download directory.
        var orphan = path + "." + Path.GetRandomFileName() + ".tmp";
        await File.WriteAllTextAsync(orphan, "half-written");

        store.Delete();

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(orphan));
    }
}
