using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Catalogs;

namespace LamSims.Core.Tests;

/// <summary>
/// The catalog this repository ships for real use, as opposed to catalog.example.json, which
/// exists to document the format and names no reachable host. Nothing here contacts one either:
/// the file is read off disk and handed to the parser, and the URLs are never dialled.
///
/// The point is that a catalog nobody validates is worse than no catalog at all. CatalogParser
/// drops a bad entry rather than failing the load, so a typo costs the user one silently missing
/// pack — no banner names it, and the row simply is not there.
/// </summary>
public class CatalogLiveTests
{
    private static string Live() =>
        File.ReadAllText(Path.Combine(CatalogExampleTests.RepositoryRoot(), "catalog.live.json"));

    [Fact]
    public void The_live_catalog_parses_with_no_rejected_entries()
    {
        var result = CatalogParser.Parse(Live());

        // The rejected entries are named, not counted: the failure this guards against is one
        // pack out of a hundred going quiet, and a bare count would not say which.
        Assert.Empty(result.Rejected.Select(r => $"{r.Description}: {r.Reason}"));
        Assert.NotEmpty(result.Catalog.Packs);
        Assert.Equal(Catalog.SupportedSchemaVersion, result.Catalog.SchemaVersion);
    }

    /// <summary>
    /// Two packs sharing a code would have the parser keep one and drop the other, and sharing an
    /// install directory would have the scanner report a pack installed because its neighbour is.
    /// Neither is visible by reading the file.
    /// </summary>
    [Fact]
    public void The_live_catalog_gives_every_pack_its_own_code_and_install_directories()
    {
        var packs = CatalogParser.Parse(Live()).Catalog.Packs;

        Assert.Equal(packs.Count, packs.Select(p => p.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var dirs = packs.SelectMany(p => p.InstallDirs).ToList();
        Assert.Equal(dirs.Count, dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
