using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Net;
using System.Text;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

public class CatalogLoaderTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string CatalogJson(string code) => $$"""
        {
          "schemaVersion": 1,
          "packs": [
            {
              "code": "{{code}}",
              "name": "Pack {{code}}",
              "type": "expansion",
              "size": 1024,
              "sha256": "{{Digest}}",
              "urls": ["https://host-a.example.invalid/{{code}}.zip"]
            }
          ]
        }
        """;

    private static CatalogLoader Loader(TempDir temp, HttpClient? client = null, string? executableDirectory = null) =>
        new(client ?? new HttpClient(),
            new AppPaths(Path.Combine(temp.Path, "appdata")),
            executableDirectory ?? Path.Combine(temp.Path, "bin"));

    private static string WriteCatalog(TempDir temp, string relativePath, string code)
    {
        var full = Path.Combine(temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, CatalogJson(code));
        return full;
    }

    [Fact]
    public async Task Prefers_the_command_line_source()
    {
        using var temp = new TempDir();
        var commandLine = WriteCatalog(temp, "cli/catalog.json", "EP01");
        var settings = WriteCatalog(temp, "settings/catalog.json", "EP02");

        var resolution = await Loader(temp).ResolveAsync(commandLine, settings, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal(CatalogSourceKind.CommandLine, resolution.Source!.Kind);
        Assert.Equal("EP01", Assert.Single(resolution.Load!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Refuses_a_local_catalog_larger_than_the_cap()
    {
        using var temp = new TempDir();
        var oversized = Path.Combine(temp.Path, "huge.json");
        Directory.CreateDirectory(temp.Path);

        // One byte past the 1 MB cap, and filler rather than valid JSON: the refusal has to
        // happen on length alone, before anything parses it.
        File.WriteAllBytes(oversized, new byte[(1024 * 1024) + 1]);

        var resolution = await Loader(temp).ResolveAsync(oversized, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains("exceeds", resolution.Error);
    }

    [Fact]
    public async Task Loads_a_local_catalog_written_with_a_byte_order_mark()
    {
        using var temp = new TempDir();
        var withBom = Path.Combine(temp.Path, "bom.json");
        Directory.CreateDirectory(temp.Path);

        // Notepad and PowerShell's Out-File both do this. The BOM must be consumed the same way
        // it is over HTTP, or the catalog reaches the parser as a U+FEFF it rejects.
        File.WriteAllText(withBom, CatalogJson("EP09"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var resolution = await Loader(temp).ResolveAsync(withBom, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal("EP09", Assert.Single(resolution.Load!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Falls_back_to_the_settings_source()
    {
        using var temp = new TempDir();
        var settings = WriteCatalog(temp, "settings/catalog.json", "EP02");

        var resolution = await Loader(temp).ResolveAsync(null, settings, CancellationToken.None);

        Assert.Equal(CatalogSourceKind.Settings, resolution.Source!.Kind);
        Assert.Equal("EP02", Assert.Single(resolution.Load!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Falls_back_to_the_catalog_beside_the_executable()
    {
        using var temp = new TempDir();
        WriteCatalog(temp, "bin/catalog.json", "EP03");

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        Assert.Equal(CatalogSourceKind.BesideExecutable, resolution.Source!.Kind);
        Assert.Equal("EP03", Assert.Single(resolution.Load!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Falls_back_to_the_cached_copy()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        Assert.Equal(CatalogSourceKind.Cache, resolution.Source!.Kind);
        Assert.Equal("EP04", Assert.Single(resolution.Load!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Reports_an_empty_state_when_nothing_resolves()
    {
        using var temp = new TempDir();

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Empty, resolution.Status);
        Assert.Null(resolution.Error);
        Assert.Null(resolution.Load);
        Assert.Null(resolution.CachedCopy);
    }

    [Fact]
    public async Task Surfaces_a_command_line_source_that_does_not_exist()
    {
        using var temp = new TempDir();
        WriteCatalog(temp, "bin/catalog.json", "EP03");
        var missing = Path.Combine(temp.Path, "typo.json");

        var resolution = await Loader(temp).ResolveAsync(missing, null, CancellationToken.None);

        // Falling through to the catalog beside the executable would hide the user's typo.
        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Equal(CatalogSourceKind.CommandLine, resolution.Source!.Kind);
        Assert.Contains("typo.json", resolution.Error);
    }

    [Fact]
    public async Task Surfaces_a_settings_source_that_fails_to_parse_and_offers_the_cache()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        var broken = Path.Combine(temp.Path, "broken.json");
        await File.WriteAllTextAsync(broken, "{ not json");

        var resolution = await Loader(temp).ResolveAsync(null, broken, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains("broken.json", resolution.Error);
        Assert.NotNull(resolution.CachedCopy);
        Assert.Null(resolution.Load);
    }

    [Fact]
    public async Task Offers_no_cached_copy_when_the_cache_is_reached_and_fails_to_parse()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, "{ not json");

        // With no other candidate the loop reaches the cache and tries to parse it.
        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        // The cache is a fallback nobody asked for, so its own failure is passed over rather
        // than reported; the resolution is Empty, as if the cache did not exist.
        Assert.Equal(CatalogStatus.Empty, resolution.Status);

        // The "use the cached copy" button would otherwise point at a file this call already
        // watched fail to parse.
        Assert.Null(resolution.CachedCopy);
    }

    [Fact]
    public async Task Still_offers_a_corrupt_cache_that_nothing_tried_during_this_resolution()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, "{ not json");

        var commandLine = WriteCatalog(temp, "cli/catalog.json", "EP01");

        var resolution = await Loader(temp).ResolveAsync(commandLine, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);

        // The command line succeeded outright, so the loop never touched the cache. Its
        // validity is unknown and it is still offered; nothing pre-validates it on every call.
        Assert.NotNull(resolution.CachedCopy);
    }

    [Fact]
    public async Task Loads_the_cache_when_it_is_chosen_explicitly_after_a_failure()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        var loader = Loader(temp);
        var chosen = new CatalogSource(CatalogSourceKind.Cache, paths.CatalogCacheFile);

        var resolution = await loader.LoadAsync(chosen, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal("EP04", Assert.Single(resolution.Load!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Reports_a_chosen_source_that_fails_as_failed_instead_of_throwing()
    {
        using var temp = new TempDir();
        var loader = Loader(temp);
        var chosen = new CatalogSource(CatalogSourceKind.Cache, Path.Combine(temp.Path, "missing.json"));

        var resolution = await loader.LoadAsync(chosen, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Equal(chosen, resolution.Source);
        Assert.Contains("missing.json", resolution.Error);
    }

    [Fact]
    public async Task Passes_over_a_damaged_catalog_beside_the_executable()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "bin", "catalog.json"), "{ not json");

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        // Nobody chose this file, so it is a fallback that did not work out, not an error.
        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal(CatalogSourceKind.Cache, resolution.Source!.Kind);
    }

    [Fact]
    public async Task Fetches_a_remote_catalog_and_caches_it()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(Encoding.UTF8.GetBytes(CatalogJson("EP05")));
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));
        var url = server.FileUrl.ToString();

        var resolution = await loader.ResolveAsync(url, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal("EP05", Assert.Single(resolution.Load!.Catalog.Packs).Code);
        Assert.True(File.Exists(paths.CatalogCacheFile));
        Assert.Equal(CatalogJson("EP05"), await File.ReadAllTextAsync(paths.CatalogCacheFile));
        Assert.Empty(Directory.GetFiles(paths.Root, "*.tmp"));
    }

    [Fact]
    public async Task Rejects_a_remote_catalog_larger_than_the_size_cap()
    {
        using var temp = new TempDir();
        var oversized = new byte[1_200_000];
        await using var server = await TestFileServer.StartAsync(oversized);
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));
        var url = server.FileUrl.ToString();

        // The loader streams the response instead of letting HttpClient buffer it, so an
        // oversized body is refused partway through rather than after it is held in full.
        var resolution = await loader.ResolveAsync(url, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains(url, resolution.Error);
        Assert.False(File.Exists(paths.CatalogCacheFile));

        // The cap's own message, matching the local test. Without it this passes with the memory
        // bound deleted: 1.2 MB of zero bytes is not JSON, so the parser fails anyway and Failed,
        // the url in the error and the absent cache all still hold.
        Assert.Contains("exceeds", resolution.Error);
    }

    [Fact]
    public async Task Loads_a_remote_catalog_served_with_a_utf8_bom()
    {
        using var temp = new TempDir();

        // A catalog authored on Windows (Notepad, PowerShell's Out-File default) commonly
        // carries a leading UTF-8 BOM; the loader must strip it the same way
        // File.ReadAllTextAsync already does for a local file.
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(CatalogJson("EP06"))).ToArray();
        await using var server = await TestFileServer.StartAsync(withBom);
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));

        var resolution = await loader.ResolveAsync(server.FileUrl.ToString(), null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal("EP06", Assert.Single(resolution.Load!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Surfaces_a_remote_catalog_that_answers_with_an_error()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(
            Encoding.UTF8.GetBytes(CatalogJson("EP05")),
            new TestFileServerOptions { FailNextRequests = 1 });
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));
        var url = server.FileUrl.ToString();

        // The loader makes one attempt per source; unlike the download engine it does not
        // retry, because a catalog is small and the user is waiting on the pack list.
        var resolution = await loader.ResolveAsync(url, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains(url, resolution.Error);
        Assert.False(File.Exists(paths.CatalogCacheFile));
    }

    [Fact]
    public async Task Never_caches_a_remote_body_that_does_not_parse()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(Encoding.UTF8.GetBytes("{ not json"));
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));

        var resolution = await loader.ResolveAsync(
            server.FileUrl.ToString(), null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.False(File.Exists(paths.CatalogCacheFile));
    }

    [Fact]
    public async Task Reports_a_stalling_remote_source_as_failed_instead_of_throwing()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(
            Encoding.UTF8.GetBytes(CatalogJson("EP05")),
            new TestFileServerOptions { StallBeforeHeaders = TimeSpan.FromSeconds(30) });

        // HttpFactory hands out an HttpClient with an infinite Timeout because the download
        // engine carries its own deadlines, so the loader needs a deadline of its own. The
        // short remoteTimeout below stands in for it.
        using var client = HttpFactory.Create(maxConnectionsPerServer: 4);

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(
            client, paths, Path.Combine(temp.Path, "bin"), remoteTimeout: TimeSpan.FromMilliseconds(200));
        var url = server.FileUrl.ToString();

        // The loader's own deadline fires as an OperationCanceledException that does not wrap
        // into HttpRequestException; this must still be reported as a source failure rather
        // than escaping ResolveAsync.
        var resolution = await loader.ResolveAsync(url, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains(url, resolution.Error);
        Assert.False(File.Exists(paths.CatalogCacheFile));
    }

    [Fact]
    public async Task Surfaces_a_command_line_path_the_filesystem_rejects()
    {
        using var temp = new TempDir();
        var malformed = "typo\0.json";

        var resolution = await Loader(temp).ResolveAsync(malformed, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Equal(CatalogSourceKind.CommandLine, resolution.Source!.Kind);
    }

    [Fact]
    public async Task Propagates_a_genuine_cancellation_instead_of_reporting_it()
    {
        using var temp = new TempDir();
        var commandLine = WriteCatalog(temp, "cli/catalog.json", "EP01");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Loader(temp).ResolveAsync(commandLine, null, cts.Token));
    }

    [Fact]
    public void Recognises_which_sources_are_remote()
    {
        Assert.True(new CatalogSource(CatalogSourceKind.Settings, "https://a.example.invalid/c.json").IsRemote);
        Assert.True(new CatalogSource(CatalogSourceKind.Settings, "http://a.example.invalid/c.json").IsRemote);
        Assert.False(new CatalogSource(CatalogSourceKind.Settings, "/srv/catalog.json").IsRemote);
        Assert.False(new CatalogSource(CatalogSourceKind.Settings, @"C:\catalog.json").IsRemote);
    }

    [Fact]
    public void Lists_only_the_candidates_that_are_present()
    {
        using var temp = new TempDir();

        var candidates = Loader(temp).Candidates(null, "   ");

        Assert.Empty(candidates);
    }
}
