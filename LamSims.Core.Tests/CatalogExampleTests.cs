using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using System.Text.Json;
using LamSims.Core.Catalogs;

namespace LamSims.Core.Tests;

public class CatalogExampleTests
{
    /// <summary>
    /// Walks up from the test binary to the solution file. Neither catalog.example.json nor
    /// catalog.schema.json is copied to the output directory, and a fixed relative depth would
    /// break when the output path changes.
    /// </summary>
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LamSims.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string Example() => File.ReadAllText(Path.Combine(RepositoryRoot(), "catalog.example.json"));

    [Fact]
    public void The_example_catalog_parses_with_no_rejected_entries()
    {
        var result = CatalogParser.Parse(Example());

        Assert.Empty(result.Rejected);
        Assert.NotEmpty(result.Catalog.Packs);
        Assert.Equal(Catalog.SupportedSchemaVersion, result.Catalog.SchemaVersion);
    }

    [Fact]
    public void The_example_catalog_names_one_pack_of_every_type()
    {
        var types = CatalogParser.Parse(Example()).Catalog.Packs.Select(p => p.Type).ToHashSet();

        Assert.Contains(PackType.Expansion, types);
        Assert.Contains(PackType.Game, types);
        Assert.Contains(PackType.Stuff, types);
        Assert.Contains(PackType.Kit, types);
    }

    [Fact]
    public void The_example_catalog_distributes_no_real_url()
    {
        foreach (var pack in CatalogParser.Parse(Example()).Catalog.Packs)
            foreach (var url in pack.Urls)
                Assert.EndsWith(".invalid", url.Host, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_document_is_valid_json_describing_the_catalog()
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "catalog.schema.json")));

        var root = schema.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("$schema", out _));

        var required = root.GetProperty("properties").GetProperty("packs")
            .GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToHashSet();

        // The schema and the parser must agree on what an entry cannot do without.
        Assert.Equal(
            new HashSet<string?> { "code", "name", "size", "sha256", "urls" },
            required);
    }

    [Fact]
    public void The_schema_s_code_pattern_rejects_the_same_dot_values_the_parser_rejects()
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "catalog.schema.json")));

        var pattern = schema.RootElement.GetProperty("properties").GetProperty("packs")
            .GetProperty("items").GetProperty("properties").GetProperty("code").GetProperty("pattern").GetString()!;

        var regex = new System.Text.RegularExpressions.Regex(pattern);

        // CatalogParser rejects "." and ".." as a code; a schema that still accepts them
        // would mislead an author validating against it before ever running the parser.
        Assert.DoesNotMatch(regex, ".");
        Assert.DoesNotMatch(regex, "..");
        Assert.Matches(regex, "EP01");
    }
}
