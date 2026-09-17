using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Returns_defaults_when_no_settings_file_exists()
    {
        using var temp = new TempDir();
        var loaded = new SettingsStore(new AppPaths(temp.Path)).Load();

        Assert.Null(loaded.Error);
        Assert.Null(loaded.Settings.CatalogSource);
        Assert.Null(loaded.Settings.GameDirectory);
        Assert.Null(loaded.Settings.DownloadDirectory);
        Assert.Equal(8, loaded.Settings.Connections);
    }

    [Fact]
    public async Task Round_trips_every_setting()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(new AppPaths(temp.Path));

        await store.SaveAsync(new AppSettings
        {
            CatalogSource = "https://catalog.example.invalid/catalog.json",
            GameDirectory = "/games/The Sims 4",
            DownloadDirectory = "/downloads",
            Connections = 12,
        }, CancellationToken.None);

        var loaded = store.Load();

        Assert.Null(loaded.Error);
        Assert.Equal("https://catalog.example.invalid/catalog.json", loaded.Settings.CatalogSource);
        Assert.Equal("/games/The Sims 4", loaded.Settings.GameDirectory);
        Assert.Equal("/downloads", loaded.Settings.DownloadDirectory);
        Assert.Equal(12, loaded.Settings.Connections);
    }

    [Fact]
    public async Task Creates_the_root_when_saving_into_a_directory_that_does_not_exist()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "not-created-yet"));

        await new SettingsStore(paths).SaveAsync(new AppSettings { Connections = 3 }, CancellationToken.None);

        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task Leaves_no_temporary_file_behind()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);

        await new SettingsStore(paths).SaveAsync(new AppSettings(), CancellationToken.None);

        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void Falls_back_to_defaults_and_reports_a_damaged_file()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        temp.Write("settings.json", "{ not json");

        var loaded = new SettingsStore(paths).Load();

        Assert.NotNull(loaded.Error);
        Assert.Contains("settings.json", loaded.Error);
        Assert.Equal(8, loaded.Settings.Connections);

        // Overwriting the file silently destroys whatever the user was hand-editing.
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void Falls_back_to_defaults_when_the_file_holds_json_that_is_not_an_object()
    {
        using var temp = new TempDir();
        temp.Write("settings.json", "[]");

        var loaded = new SettingsStore(new AppPaths(temp.Path)).Load();

        Assert.NotNull(loaded.Error);
        Assert.Equal(8, loaded.Settings.Connections);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 16)]
    [InlineData(12, 12)]
    public void Clamps_a_hand_edited_connection_count_into_range(int stored, int expected)
    {
        var options = new AppSettings { Connections = stored }.ToDownloadOptions();

        Assert.Equal(expected, options.Connections);
    }
}
