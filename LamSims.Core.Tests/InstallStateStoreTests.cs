using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Text.Json;
using LamSims.Core.Installing;

namespace LamSims.Core.Tests;

public class InstallStateStoreTests
{
    private const string Digest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static InstallStateStore Store(TempDir temp) =>
        new(Path.Combine(temp.Path, "installs"));

    private static InstallMarker Marker(
        string code, string gameDirectory,
        InstallMarkerStatus status = InstallMarkerStatus.Installed,
        string? digest = null,
        DateTimeOffset? updated = null) =>
        new(InstallMarker.CurrentSchemaVersion, code, gameDirectory, digest ?? Digest, status,
            updated ?? new DateTimeOffset(2026, 8, 15, 14, 3, 22, 117, TimeSpan.Zero));

    [Fact]
    public async Task Round_trips_a_marker()
    {
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        await store.SaveAsync(Marker("EP01", game), CancellationToken.None);
        var loaded = store.TryLoad(game, "EP01");

        Assert.NotNull(loaded);
        Assert.Equal("EP01", loaded.Code);
        Assert.Equal(game, loaded.GameDirectory);
        Assert.Equal(Digest, loaded.ArchiveSha256);
        Assert.Equal(InstallMarkerStatus.Installed, loaded.Status);

        // Exact ticks: the GUI shows this timestamp and the format has to round-trip it.
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 14, 3, 22, 117, TimeSpan.Zero), loaded.UpdatedUtc);
    }

    [Fact]
    public async Task Writes_the_status_as_a_camel_case_string()
    {
        // The on-disk format is a contract with the user, who may read or hand-edit it. A
        // numeric enum would also make an added enum case silently renumber existing files.
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        await store.SaveAsync(Marker("EP01", game, InstallMarkerStatus.Installing), CancellationToken.None);

        var json = await File.ReadAllTextAsync(store.MarkerFile(game, "EP01"));
        Assert.Contains("\"status\": \"installing\"", json);
        Assert.Contains("\"schemaVersion\": 1", json);
    }

    [Fact]
    public void An_absent_marker_is_null_rather_than_an_exception()
    {
        using var temp = new TempDir();

        Assert.Null(Store(temp).TryLoad(Path.Combine(temp.Path, "game"), "EP01"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP0")]
    [InlineData("\u0000\u0001\u0002 not json at all")]
    [InlineData("{\"schemaVersion\": 2, \"code\": \"EP01\", \"gameDirectory\": \"/g\", \"archiveSha256\": \"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\", \"status\": \"installed\", \"updatedUtc\": \"2026-08-15T14:03:22Z\"}")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP01\", \"gameDirectory\": \"/g\", \"archiveSha256\": \"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\", \"status\": \"reticulating\", \"updatedUtc\": \"2026-08-15T14:03:22Z\"}")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP01\", \"gameDirectory\": \"/g\", \"archiveSha256\": \"xyz\", \"status\": \"installed\", \"updatedUtc\": \"2026-08-15T14:03:22Z\"}")]
    [InlineData("{\"schemaVersion\": 1, \"code\": \"EP01\", \"gameDirectory\": \"/g\", \"archiveSha256\": \"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\", \"updatedUtc\": \"2026-08-15T14:03:22Z\"}")]
    public async Task A_marker_that_does_not_parse_cleanly_reads_as_absent(string contents)
    {
        // The last case matters most: an absent 'status' must not default to Installing, or a
        // truncated write accuses a healthy install of being interrupted.
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        await store.SaveAsync(Marker("EP01", game), CancellationToken.None);
        await File.WriteAllTextAsync(store.MarkerFile(game, "EP01"), contents);

        Assert.Null(store.TryLoad(game, "EP01"));
        Assert.Empty(store.LoadAll(game));
    }

    [Fact]
    public async Task Keeps_markers_for_two_game_directories_apart()
    {
        // Pointing GameDirectory at a second drive must not demote the first drive's library.
        using var temp = new TempDir();
        var store = Store(temp);
        var one = Path.Combine(temp.Path, "game-one");
        var two = Path.Combine(temp.Path, "game-two");

        await store.SaveAsync(Marker("EP01", one), CancellationToken.None);
        await store.SaveAsync(Marker("EP01", two, InstallMarkerStatus.Installing), CancellationToken.None);

        Assert.Equal(InstallMarkerStatus.Installed, store.TryLoad(one, "EP01")!.Status);
        Assert.Equal(InstallMarkerStatus.Installing, store.TryLoad(two, "EP01")!.Status);
    }

    [Fact]
    public async Task Treats_a_trailing_separator_and_a_relative_path_as_the_same_directory()
    {
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(game);

        await store.SaveAsync(Marker("EP01", game), CancellationToken.None);

        Assert.NotNull(store.TryLoad(game + Path.DirectorySeparatorChar, "EP01"));
        Assert.NotNull(store.TryLoad(Path.Combine(game, "..", "game"), "EP01"));
    }

    [Fact]
    public async Task Loads_every_marker_for_one_game_directory_keyed_by_code()
    {
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        await store.SaveAsync(Marker("EP01", game), CancellationToken.None);
        await store.SaveAsync(Marker("SP30", game, InstallMarkerStatus.Installing), CancellationToken.None);

        var all = store.LoadAll(game);

        Assert.Equal(2, all.Count);
        Assert.Equal(InstallMarkerStatus.Installed, all["EP01"].Status);

        // The catalog parser de-duplicates codes case-insensitively; a lookup that disagreed
        // would report a pack as unverified purely over case.
        Assert.Equal(InstallMarkerStatus.Installing, all["sp30"].Status);
    }

    [Fact]
    public void Loading_a_game_directory_that_has_no_markers_is_empty_rather_than_an_error()
    {
        using var temp = new TempDir();

        Assert.Empty(Store(temp).LoadAll(Path.Combine(temp.Path, "never-installed-into")));
    }

    [Fact]
    public async Task Ignores_the_temporary_file_an_interrupted_write_leaves_behind()
    {
        // AtomicFile writes '<path>.<random>.tmp' beside the marker; a kill mid-save leaves one.
        // Parsing it as a marker would double-count a pack.
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        await store.SaveAsync(Marker("EP01", game), CancellationToken.None);
        var marker = store.MarkerFile(game, "EP01");
        File.Copy(marker, marker + ".abcdefgh.tmp");

        Assert.Single(store.LoadAll(game));
    }

    [Fact]
    public async Task Resolves_duplicate_codes_to_the_most_recently_updated()
    {
        // Only reachable on a case-sensitive filesystem fed by hand-placed files, but the
        // winner has to be deterministic rather than whatever the directory listing yields.
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        var older = Marker("EP01", game, InstallMarkerStatus.Installing,
            updated: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Marker("EP01", game, InstallMarkerStatus.Installed,
            updated: new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));

        await store.SaveAsync(older, CancellationToken.None);
        var handPlaced = Path.Combine(Path.GetDirectoryName(store.MarkerFile(game, "EP01"))!, "Ep01.json");
        await File.WriteAllTextAsync(handPlaced, JsonSerializer.Serialize(newer, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        }));

        Assert.Equal(InstallMarkerStatus.Installed, store.LoadAll(game)["EP01"].Status);
    }

    [Fact]
    public async Task Names_the_marker_file_after_the_lower_cased_code()
    {
        // Codes differing only in case must not become two files on ext4 and one on NTFS.
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        await store.SaveAsync(Marker("EP01", game), CancellationToken.None);

        Assert.Equal("ep01.json", Path.GetFileName(store.MarkerFile(game, "EP01")));
        Assert.Equal(store.MarkerFile(game, "EP01"), store.MarkerFile(game, "ep01"));
    }

    [Fact]
    public void Refuses_a_code_that_is_not_a_file_name()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");

        Assert.Throws<ArgumentException>(() => Store(temp).MarkerFile(game, "../escape"));
        Assert.Throws<ArgumentException>(() => Store(temp).MarkerFile(game, "  "));
    }

    [Fact]
    public async Task Puts_markers_under_the_configured_root()
    {
        using var temp = new TempDir();
        var store = Store(temp);
        var game = Path.Combine(temp.Path, "game");

        await store.SaveAsync(Marker("EP01", game), CancellationToken.None);

        Assert.Equal(Path.Combine(temp.Path, "installs"), store.Root);
        Assert.StartsWith(store.Root, store.MarkerFile(game, "EP01"));
    }
}
