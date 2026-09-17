using System;
using Xunit;
using LamSims.Core;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class FileNameRulesTests
{
    [Theory]
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("CON")]
    [InlineData("AUX")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("NUL.part")]
    [InlineData("com3.zip")]
    [InlineData("NUL.part.tmp")]
    public void Reserved_device_names_are_recognised(string name) =>
        Assert.True(FileNameRules.IsReservedDeviceName(name));

    [Theory]
    [InlineData("EP01")]
    [InlineData("NULL")]          // four letters: a file, not the device
    [InlineData("COM0")]          // only COM1-COM9 are devices
    [InlineData("LPT")]
    [InlineData("a.NUL")]         // the stem is what matters, not the extension
    [InlineData("ea_app_version.dll")]
    public void Ordinary_names_are_not_reserved(string name) =>
        Assert.False(FileNameRules.IsReservedDeviceName(name));

    [Theory]
    [InlineData("EP01.")]
    [InlineData("EP01 ")]
    [InlineData("EP01. ")]
    public void Trailing_dots_and_spaces_are_recognised(string name) =>
        Assert.True(FileNameRules.HasTrailingDotOrSpace(name));

    [Theory]
    [InlineData("EP01")]
    [InlineData("EP01.zip")]
    [InlineData(" EP01")]
    public void Names_without_a_trailing_dot_or_space_are_not(string name) =>
        Assert.False(FileNameRules.HasTrailingDotOrSpace(name));

    [Fact]
    public void DownloadPaths_refuses_a_reserved_device_name()
    {
        var paths = new DownloadPaths("/downloads");

        var e = Assert.Throws<ArgumentException>(() => paths.PartFile("NUL"));
        Assert.Contains("NUL", e.Message);
    }

    [Fact]
    public void DownloadPaths_refuses_a_trailing_dot()
    {
        var paths = new DownloadPaths("/downloads");

        Assert.Throws<ArgumentException>(() => paths.ArchiveFile("EP01."));
    }

    [Fact]
    public void DownloadPaths_refuses_a_reserved_unlocker_asset_name()
    {
        var paths = new DownloadPaths("/downloads");

        Assert.Throws<ArgumentException>(() => paths.UnlockerAssetFile("PRN"));
    }

    /// <summary>
    /// Parse rejects the entry instead of throwing, so a single bad pack does not cost the user the
    /// rest. Rejected is asserted alongside the absence, since a silent drop satisfies either alone.
    /// </summary>
    [Fact]
    public void The_catalog_rejects_a_reserved_device_code()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "packs": [
            {
              "code": "NUL",
              "name": "Null Device",
              "size": 1024,
              "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "urls": ["https://example.invalid/nul.zip"]
            }
          ]
        }
        """;

        var result = CatalogParser.Parse(json);

        Assert.Empty(result.Catalog.Packs);
        var rejected = Assert.Single(result.Rejected);
        Assert.Contains("NUL", rejected.Description);
    }

    [Fact]
    public void The_catalog_rejects_a_code_with_a_trailing_dot()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "packs": [
            {
              "code": "EP01.",
              "name": "Trailing Dot",
              "size": 1024,
              "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "urls": ["https://example.invalid/ep01.zip"]
            }
          ]
        }
        """;

        var result = CatalogParser.Parse(json);

        Assert.Empty(result.Catalog.Packs);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void The_catalog_rejects_a_reserved_device_install_dir()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "packs": [
            {
              "code": "EP01",
              "name": "Reserved Install Dir",
              "size": 1024,
              "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "urls": ["https://example.invalid/ep01.zip"],
              "installDirs": ["NUL"]
            }
          ]
        }
        """;

        var result = CatalogParser.Parse(json);

        Assert.Empty(result.Catalog.Packs);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void The_catalog_rejects_an_install_dir_with_a_trailing_dot()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "packs": [
            {
              "code": "EP01",
              "name": "Trailing Dot Install Dir",
              "size": 1024,
              "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "urls": ["https://example.invalid/ep01.zip"],
              "installDirs": ["Data."]
            }
          ]
        }
        """;

        var result = CatalogParser.Parse(json);

        Assert.Empty(result.Catalog.Packs);
        Assert.Single(result.Rejected);
    }

    [Theory]
    [InlineData("EP01?")]
    [InlineData("EP01*")]
    [InlineData("EP01|x")]
    [InlineData("C:evil")]
    [InlineData("back\\slash")]
    [InlineData("for/ward")]
    [InlineData("quote\"d")]
    [InlineData("lt<gt>")]
    public void Characters_no_platform_allows_are_rejected(string name) =>
        Assert.True(FileNameRules.HasInvalidCharacter(name));

    [Theory]
    [InlineData("EP01")]
    [InlineData("ea_app_version.dll")]
    [InlineData("The Sims 4")]
    [InlineData("a-b_c.d")]
    public void Ordinary_names_have_no_invalid_character(string name) =>
        Assert.False(FileNameRules.HasInvalidCharacter(name));

    [Fact]
    public void A_windows_illegal_character_is_rejected_on_every_platform()
    {
        var paths = new DownloadPaths("/downloads");

        // Not gated on OperatingSystem: the point is that this throws here, on Linux, where
        // Path.GetInvalidFileNameChars() would have allowed it.
        Assert.Throws<ArgumentException>(() => paths.ArchiveFile("EP01?"));
    }
}
