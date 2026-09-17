using System;
using Xunit;
using LamSims.Core.Catalogs;

namespace LamSims.Core.Tests;

public class CatalogParserTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Json(string packs, int schemaVersion = 1) => $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "updatedUtc": "2026-08-14T12:00:00Z",
          "packs": [{{packs}}]
        }
        """;

    private static string Pack(
        string code = "EP01",
        string name = "The Sims 4 Get to Work",
        string type = "expansion",
        string size = "6871947673",
        string sha256 = Digest,
        string urls = """["https://host-a.example.invalid/EP01.zip"]""",
        string? extra = null) => $$"""
        {
          "code": {{(code is "null" ? "null" : $"\"{code}\"")}},
          "name": "{{name}}",
          "type": "{{type}}",
          "size": {{size}},
          "sha256": {{(sha256 is "null" ? "null" : $"\"{sha256}\"")}},
          "urls": {{urls}}{{(extra is null ? "" : "," + extra)}}
        }
        """;

    [Fact]
    public void Reads_a_valid_entry()
    {
        var extra = "\"installedSize\": 7300000000, \"installDirs\": [\"EP01\"]";
        var result = CatalogParser.Parse(Json(Pack(extra: extra)));

        Assert.Empty(result.Rejected);
        var pack = Assert.Single(result.Catalog.Packs);
        Assert.Equal("EP01", pack.Code);
        Assert.Equal("The Sims 4 Get to Work", pack.Name);
        Assert.Equal(PackType.Expansion, pack.Type);
        Assert.Equal(6871947673, pack.Size);
        Assert.Equal(7300000000, pack.InstalledSize);
        Assert.Equal(7300000000, pack.RequiredInstallBytes);
        Assert.Equal(Digest, pack.Sha256);
        Assert.Equal(new Uri("https://host-a.example.invalid/EP01.zip"), Assert.Single(pack.Urls));
        Assert.Equal("EP01", Assert.Single(pack.InstallDirs));
        Assert.Equal(1, result.Catalog.SchemaVersion);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero), result.Catalog.UpdatedUtc);
    }

    [Fact]
    public void Defaults_install_dirs_to_the_code_and_install_size_to_size()
    {
        var pack = Assert.Single(CatalogParser.Parse(Json(Pack())).Catalog.Packs);

        Assert.Equal("EP01", Assert.Single(pack.InstallDirs));
        Assert.Null(pack.InstalledSize);
        Assert.Equal(6871947673, pack.RequiredInstallBytes);
    }

    [Fact]
    public void Maps_an_entry_onto_a_download_request()
    {
        var pack = Assert.Single(CatalogParser.Parse(Json(Pack())).Catalog.Packs);
        var request = pack.ToDownloadRequest();

        Assert.Equal("EP01", request.Code);
        Assert.Equal(6871947673, request.Size);
        Assert.Equal(Digest, request.Sha256);
        Assert.Equal(pack.Urls, request.Urls);
    }

    [Theory]
    [InlineData("expansion", PackType.Expansion)]
    [InlineData("GAME", PackType.Game)]
    [InlineData("stuff", PackType.Stuff)]
    [InlineData("kit", PackType.Kit)]
    [InlineData("bundle", PackType.Unknown)]
    // Enum.TryParse accepts the numeric form too, so without a defined-value check these would
    // reach the application as a PackType nothing has a case for.
    [InlineData("7", PackType.Unknown)]
    [InlineData("-1", PackType.Unknown)]
    public void Reads_the_pack_type_case_insensitively(string type, PackType expected)
    {
        var pack = Assert.Single(CatalogParser.Parse(Json(Pack(type: type))).Catalog.Packs);
        Assert.Equal(expected, pack.Type);
    }

    [Fact]
    public void Treats_a_missing_type_as_unknown()
    {
        var result = CatalogParser.Parse(Json($$"""
            { "code": "EP01", "name": "Get to Work", "size": 100, "sha256": "{{Digest}}",
              "urls": ["https://host-a.example.invalid/EP01.zip"] }
            """));

        Assert.Equal(PackType.Unknown, Assert.Single(result.Catalog.Packs).Type);
    }

    [Fact]
    public void Refuses_an_unknown_schema_version()
    {
        var error = Assert.Throws<CatalogFormatException>(() => CatalogParser.Parse(Json(Pack(), schemaVersion: 2)));
        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void Refuses_malformed_json()
    {
        Assert.Throws<CatalogFormatException>(() => CatalogParser.Parse("{ \"schemaVersion\": 1, "));
    }

    [Fact]
    public void Refuses_a_catalog_without_a_packs_array()
    {
        Assert.Throws<CatalogFormatException>(() => CatalogParser.Parse("""{ "schemaVersion": 1 }"""));
    }

    [Theory]
    [InlineData("null", "code")]
    [InlineData("", "code")]
    public void Rejects_an_entry_without_a_code(string code, string expected)
    {
        var result = CatalogParser.Parse(Json(Pack(code: code)));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains(expected, Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_code_carrying_path_characters()
    {
        var result = CatalogParser.Parse(Json(Pack(code: "../EP01")));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("../EP01", Assert.Single(result.Rejected).Description);
    }

    [Fact]
    public void Rejects_a_code_of_exactly_dot_dot()
    {
        var result = CatalogParser.Parse(Json(Pack(code: "..")));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("not a usable file name", Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_code_of_exactly_dot()
    {
        var result = CatalogParser.Parse(Json(Pack(code: ".")));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("not a usable file name", Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_install_dirs_entry_of_dot_dot()
    {
        var extra = "\"installDirs\": [\"..\"]";
        var result = CatalogParser.Parse(Json(Pack(extra: extra)));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("..", Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public void Rejects_an_entry_without_a_checksum()
    {
        var result = CatalogParser.Parse(Json(Pack(sha256: "null")));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("sha256", Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_checksum_that_cannot_be_a_digest()
    {
        var result = CatalogParser.Parse(Json(Pack(sha256: "not-a-digest")));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("sha256", Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_entry_without_urls()
    {
        var result = CatalogParser.Parse(Json(Pack(urls: "[]")));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("url", Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_url_that_is_not_http()
    {
        var result = CatalogParser.Parse(Json(Pack(urls: """["file:///etc/passwd"]""")));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("file:///etc/passwd", Assert.Single(result.Rejected).Reason);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Rejects_a_non_positive_size(string size)
    {
        var result = CatalogParser.Parse(Json(Pack(size: size)));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("size", Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_the_second_entry_carrying_a_duplicate_code()
    {
        var result = CatalogParser.Parse(Json(Pack() + "," + Pack(name: "A Different Name")));

        var kept = Assert.Single(result.Catalog.Packs);
        Assert.Equal("The Sims 4 Get to Work", kept.Name);
        Assert.Contains("EP01", Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public void Keeps_the_remaining_entries_when_one_is_rejected()
    {
        var result = CatalogParser.Parse(Json(Pack(sha256: "null") + "," + Pack(code: "EP02")));

        Assert.Equal("EP02", Assert.Single(result.Catalog.Packs).Code);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void Names_a_rejected_entry_by_its_position_when_it_has_no_code()
    {
        var result = CatalogParser.Parse(Json(Pack(code: "null")));

        Assert.Contains("1", Assert.Single(result.Rejected).Description);
    }

    [Fact]
    public void Accepts_a_catalog_with_no_packs()
    {
        var result = CatalogParser.Parse("""{ "schemaVersion": 1, "packs": [] }""");

        Assert.Empty(result.Catalog.Packs);
        Assert.Empty(result.Rejected);
        Assert.Null(result.Catalog.UpdatedUtc);
    }

    [Fact]
    public void Rejects_an_installed_size_that_is_not_a_number()
    {
        var extra = "\"installedSize\": \"7 GB\"";
        var result = CatalogParser.Parse(Json(Pack(extra: extra)));

        Assert.Empty(result.Catalog.Packs);
        Assert.Contains("installedSize", Assert.Single(result.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnownCodes_matches_the_catalogs_packs_case_insensitively()
    {
        var result = CatalogParser.Parse(Json(Pack(code: "EP01") + "," + Pack(code: "EP02")));

        Assert.Equal(2, result.Catalog.KnownCodes.Count);
        Assert.Contains("EP01", result.Catalog.KnownCodes);
        Assert.Contains("ep01", result.Catalog.KnownCodes);
        Assert.DoesNotContain("EP99", result.Catalog.KnownCodes);
    }
}
