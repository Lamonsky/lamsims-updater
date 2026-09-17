using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Catalogs;
using LamSims.Core.Logging;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

public class CatalogLoggingTests
{
    [Fact]
    public async Task Loading_a_catalog_logs_the_source_and_the_counts()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();
        dir.Write("catalog.json", CatalogFixture.TwoPacksOneBad);
        var file = dir.File("catalog.json");
        var loader = CatalogFixture.Loader(dir, log);

        var resolution = await loader.LoadAsync(
            new CatalogSource(CatalogSourceKind.Settings, file), CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.True(log.Logged($"Loading a catalog from Settings: {file}"));
        Assert.True(log.Logged("Loaded 2 packs, 1 entry rejected"));
    }

    /// <summary>
    /// One line per rejected entry, naming it. The banner only says how many were dropped and
    /// quotes the first, so without this the rest are invisible. Checks both the count (one
    /// warning, matching the fixture's single reject) and that the entry is actually named
    /// rather than a generic "something was rejected" line: a fragment match on "rejected"
    /// alone would still pass if every reject collapsed into one unnamed line, or if the
    /// warning dropped <c>rejected.Description</c> entirely.
    /// </summary>
    [Fact]
    public async Task Every_rejected_entry_is_named_as_a_warning()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();
        dir.Write("catalog.json", CatalogFixture.TwoPacksOneBad);
        var file = dir.File("catalog.json");

        await CatalogFixture.Loader(dir, log).LoadAsync(
            new CatalogSource(CatalogSourceKind.Settings, file), CancellationToken.None);

        var rejectWarnings = log.Lines
            .Where(l => l.Severity == LogSeverity.Warning
                && l.Text.Contains("rejected", StringComparison.Ordinal))
            .ToArray();

        // The fixture has exactly one bad entry, so exactly one warning must name it - not one
        // combined line, not one per accepted pack, not a second line for the same reject.
        var warning = Assert.Single(rejectWarnings);

        // "entry 3" is the bad entry's 1-based position in TwoPacksOneBad's "packs" array, and
        // "('..')" is the code CatalogParser.Describe quotes for it - together they are what
        // CatalogParser.Describe names it, so this proves the line actually identifies the
        // rejected entry rather than merely mentioning the word "rejected".
        Assert.Contains("entry 3 ('..')", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_load_logs_the_reason_as_an_error()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();

        var resolution = await CatalogFixture.Loader(dir, log).LoadAsync(
            new CatalogSource(CatalogSourceKind.Settings, dir.File("missing.json")),
            CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.True(log.Logged("Catalog load failed", LogSeverity.Error));
    }

    /// <summary>
    /// Loading through <see cref="CatalogLoader.ResolveAsync"/> - the normal app-startup path -
    /// must log exactly like <see cref="CatalogLoader.LoadAsync"/> does, since both funnel
    /// through the same private LoadCoreAsync. Without this, a completely ordinary launch would
    /// log nothing at all.
    /// </summary>
    [Fact]
    public async Task Resolving_a_catalog_logs_the_source_and_the_counts()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();
        dir.Write("catalog.json", CatalogFixture.TwoPacksOneBad);
        var file = dir.File("catalog.json");

        var resolution = await CatalogFixture.Loader(dir, log).ResolveAsync(
            file, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.True(log.Logged($"Loading a catalog from CommandLine: {file}"));
        Assert.True(log.Logged("Loaded 2 packs, 1 entry rejected"));
    }

    /// <summary>
    /// The command-line source is the user's explicit choice; a failure there is reported and
    /// ends the resolution (see <see cref="CatalogLoader.ResolveAsync"/>'s
    /// CommandLine/Settings check). That failure must still be logged even though it never
    /// reaches <see cref="CatalogLoader.LoadAsync"/>.
    /// </summary>
    [Fact]
    public async Task A_failed_first_candidate_during_resolve_is_reported_and_logged()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();

        var resolution = await CatalogFixture.Loader(dir, log).ResolveAsync(
            dir.File("missing.json"), null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.True(log.Logged("Catalog load failed", LogSeverity.Error));
    }

    /// <summary>
    /// The cache is a fallback nobody asked for, so <see cref="CatalogLoader.ResolveAsync"/>
    /// steps over a broken one silently rather than reporting it - the resolution itself comes
    /// back Empty, as if the cache did not exist. Silent to the caller must not mean silent in
    /// the log: this is the exact scenario the brief's coordinator called out, so it gets its
    /// own test rather than relying on the reported-failure test above to imply it.
    /// </summary>
    [Fact]
    public async Task A_stepped_over_fallback_candidate_during_resolve_is_still_logged()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();
        var paths = CatalogFixture.Paths(dir);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, "{ not json");

        // No command line, no settings value, and no catalog.json beside the executable, so the
        // only candidate Candidates() offers is the corrupt cache.
        var resolution = await CatalogFixture.Loader(dir, log).ResolveAsync(
            null, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Empty, resolution.Status);
        Assert.True(log.Logged("Catalog load failed", LogSeverity.Error));
    }
}

/// <summary>Fixtures shared by the catalog logging tests.</summary>
internal static class CatalogFixture
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Two valid packs and a third entry whose code is "..", which <see cref="CatalogParser"/> rejects.</summary>
    public static readonly string TwoPacksOneBad = $$"""
        {
          "schemaVersion": 1,
          "packs": [
            {
              "code": "pack-a",
              "name": "Pack A",
              "type": "expansion",
              "size": 1024,
              "sha256": "{{Digest}}",
              "urls": ["https://host-a.example.invalid/pack-a.zip"]
            },
            {
              "code": "pack-b",
              "name": "Pack B",
              "type": "expansion",
              "size": 1024,
              "sha256": "{{Digest}}",
              "urls": ["https://host-b.example.invalid/pack-b.zip"]
            },
            {
              "code": "..",
              "name": "Bad",
              "type": "expansion",
              "size": 1024,
              "sha256": "{{Digest}}",
              "urls": ["https://host-c.example.invalid/bad.zip"]
            }
          ]
        }
        """;

    public static AppPaths Paths(TempDir dir) => new(Path.Combine(dir.Path, "appdata"));

    public static CatalogLoader Loader(TempDir dir, ILogSink log) =>
        new(new HttpClient(),
            Paths(dir),
            Path.Combine(dir.Path, "bin"),
            log: log);
}
