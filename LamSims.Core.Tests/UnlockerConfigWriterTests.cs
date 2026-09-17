using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class UnlockerConfigWriterTests
{
    private static UnlockerPaths Paths(TempDir dir) =>
        new(Path.Combine(dir.Path, "roaming"), Path.Combine(dir.Path, "common"));

    /// <summary>
    /// CNT is what the unlocker reads to know how many entries follow, so a CNT that disagrees
    /// with the entries either truncates the list or walks past its end. The two are asserted
    /// against each other rather than both against a literal, because the literal is what a person
    /// adding a pack forgets to update.
    /// </summary>
    [Fact]
    public void The_embedded_dlc_list_declares_as_many_packs_as_it_carries()
    {
        var text = UnlockerConfigWriter.ReadDlcList();

        var declared = int.Parse(
            Regex.Match(text, @"^CNT=(\d+)$", RegexOptions.Multiline).Groups[1].Value);
        var present = text.Split('\n').Count(l => l.StartsWith("NAM", StringComparison.Ordinal));

        Assert.Equal(present, declared);

        // Numbered from 1 with no gaps and no repeats: the unlocker indexes NAM/IID/ETG by
        // position, so a gap silently drops every entry after it.
        var numbered = Regex.Matches(text, @"^NAM(\d+)=", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value)).OrderBy(n => n).ToArray();

        Assert.Equal(Enumerable.Range(1, present).ToArray(), numbered);
    }

    /// <summary>
    /// A name is how a user identifies what an entry unlocks, and an IID is what the entry IS.
    /// Upstream's list has repeatedly carried one name on two different entitlements, which reads
    /// as a duplicate row that could be deleted.
    /// </summary>
    [Fact]
    public void No_two_dlc_list_entries_share_a_name_or_an_identifier()
    {
        var text = UnlockerConfigWriter.ReadDlcList();

        Assert.Empty(Duplicates(text, @"^NAM\d+=(.+)$"));
        Assert.Empty(Duplicates(text, @"^IID\d+=(.+)$"));
    }

    private static string[] Duplicates(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Trim())
            .GroupBy(v => v, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} (x{g.Count()})")
            .ToArray();

    [Fact]
    public void The_embedded_default_config_carries_the_user_tunable_keys()
    {
        var text = UnlockerConfigWriter.ReadDefaultConfig();

        Assert.Contains("replaceDLCs=", text);
        Assert.Contains("languages=", text);
        Assert.Contains("showMessages=", text);
        Assert.Contains("debugMode=", text);
        Assert.Contains("fakeFullGame=", text);
    }

    [Fact]
    public async Task A_first_write_creates_both_files()
    {
        using var dir = new TempDir();
        var paths = Paths(dir);

        await UnlockerConfigWriter.WriteAsync(paths, CancellationToken.None);

        Assert.True(File.Exists(paths.ConfigFile));
        Assert.True(File.Exists(paths.DlcListFile));
    }

    /// <summary>
    /// The whole point: a reinstall must refresh the DLC list (new packs
    /// appear in it) while preserving config.ini (the user tunes it). Asserting only that config.ini
    /// survived would pass against a writer that touched neither file.
    /// </summary>
    [Fact]
    public async Task A_second_write_refreshes_the_dlc_list_and_preserves_an_edited_config()
    {
        using var dir = new TempDir();
        var paths = Paths(dir);
        await UnlockerConfigWriter.WriteAsync(paths, CancellationToken.None);

        await File.WriteAllTextAsync(paths.ConfigFile, "[config]\nreplaceDLCs=1\n");
        await File.WriteAllTextAsync(paths.DlcListFile, "clobbered");

        await UnlockerConfigWriter.WriteAsync(paths, CancellationToken.None);

        Assert.Equal("[config]\nreplaceDLCs=1\n", await File.ReadAllTextAsync(paths.ConfigFile));
        Assert.Contains("CNT=160", await File.ReadAllTextAsync(paths.DlcListFile));
    }

    /// <summary>
    /// Both resources are written to disk verbatim, so every byte is part of the contract,
    /// including the config file's lack of a trailing newline and the DLC list's trailing blank
    /// line. A hash rather than a length, because a length passes for any edit that keeps the size.
    ///
    /// config.ini is still upstream's literal byte for byte. The DLC list is NOT: it carries
    /// upstream's CNT=160 entries plus four label corrections upstream has not made - NAM130 named
    /// for NAM131's incentive items, a double space in NAM135 and NAM136, and three base/PrePurchase
    /// pairs sharing one name each. The digest is therefore this fork's, and a re-adoption from
    /// upstream must re-apply those corrections rather than take the literal wholesale.
    /// </summary>
    [Theory]
    [InlineData("LamSims.Core.Unlocking.Resources.config.ini", 1180,
                "4fe4d5f182c9b1eb680d1339030fd3a67e48c7db3859c2ad74ff703d0585d872")]
    [InlineData("LamSims.Core.Unlocking.Resources.dlc-list.ini", 26278,
                "7a638237154d4c136c9e0dd2ea5cd2d884854ccbe3e0a8a00e602affde8b8a9a")]
    public void The_embedded_resources_are_upstream_byte_for_byte(string resource, int length,
                                                                 string digest)
    {
        using var stream = typeof(UnlockerConfigWriter).Assembly.GetManifestResourceStream(resource);

        // Not merely non-null: a LogicalName typo in the csproj is a runtime-only failure, and the
        // DLC list's own filename contains spaces.
        Assert.NotNull(stream);

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        Assert.Equal(length, bytes.Length);
        Assert.Equal(digest, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
}
