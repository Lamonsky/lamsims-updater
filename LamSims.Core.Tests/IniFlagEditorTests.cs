using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Text;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class IniFlagEditorTests
{
    private const string Flag = "machine.bgsstandaloneenabled=0";

    private static async Task<string> WriteFixture(TempDir dir, string content, Encoding encoding)
    {
        var path = dir.File("machine.ini");
        await File.WriteAllBytesAsync(path, encoding.GetPreamble()
            .Concat(encoding.GetBytes(content)).ToArray());
        return path;
    }

    [Fact]
    public async Task Adding_the_flag_appends_a_line()
    {
        using var dir = new TempDir();
        var path = await WriteFixture(dir, "[machine]\nfoo=1\n", new UTF8Encoding(false));

        Assert.True(await IniFlagEditor.AddFlagAsync(path, Flag, CancellationToken.None));

        // The flag belongs under the section it qualifies, and a "contains" check would also pass
        // for a line inserted above the header.
        var lines = (await File.ReadAllTextAsync(path))
            .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        Assert.Equal(Flag, lines[^1]);
    }

    [Fact]
    public async Task Adding_the_flag_twice_changes_nothing_the_second_time()
    {
        using var dir = new TempDir();
        var path = await WriteFixture(dir, "[machine]\nfoo=1\n", new UTF8Encoding(false));
        await IniFlagEditor.AddFlagAsync(path, Flag, CancellationToken.None);
        var after = await File.ReadAllBytesAsync(path);

        Assert.False(await IniFlagEditor.AddFlagAsync(path, Flag, CancellationToken.None));

        Assert.Equal(after, await File.ReadAllBytesAsync(path));
    }

    /// <summary>
    /// AtomicFile.WriteAllTextAsync cannot be used here: its StreamWriter emits no byte-order mark
    /// and "\n" endings, so a real machine.ini comes back altered. The cases cover CRLF and a BOM
    /// because a normalising writer passes without them.
    /// </summary>
    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\n", true)]
    public async Task Add_then_remove_restores_the_file_byte_for_byte(string newline, bool bom)
    {
        using var dir = new TempDir();
        var encoding = new UTF8Encoding(bom);
        var content = string.Join(newline, "[machine]", "foo=1", "bar=2") + newline;
        var path = await WriteFixture(dir, content, encoding);
        var original = await File.ReadAllBytesAsync(path);

        Assert.True(await IniFlagEditor.AddFlagAsync(path, Flag, CancellationToken.None));
        Assert.True(await IniFlagEditor.RemoveFlagAsync(path, Flag, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    /// <summary>
    /// The case upstream's unanchored Replace corrupts: a different key whose value merely contains
    /// the flag text. Removal must match whole lines, not substrings.
    /// </summary>
    [Fact]
    public async Task Removal_leaves_a_longer_key_that_merely_contains_the_flag_text_intact()
    {
        using var dir = new TempDir();
        const string other = "machine.bgsstandaloneenabled=01";
        var path = await WriteFixture(dir, $"[machine]\n{other}\n", new UTF8Encoding(false));

        Assert.False(await IniFlagEditor.RemoveFlagAsync(path, Flag, CancellationToken.None));

        Assert.Contains(other, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_missing_file_is_not_an_error_and_reports_no_change()
    {
        using var dir = new TempDir();
        var path = dir.File("absent.ini");

        Assert.False(await IniFlagEditor.AddFlagAsync(path, Flag, CancellationToken.None));
        Assert.False(await IniFlagEditor.RemoveFlagAsync(path, Flag, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    // IniFlagEditor's trailing-newline bookkeeping must not add or drop a final line ending.
    [Fact]
    public async Task Add_then_remove_restores_a_file_with_no_trailing_newline()
    {
        using var dir = new TempDir();
        var path = await WriteFixture(dir, "[machine]\nfoo=1", new UTF8Encoding(false));
        var original = await File.ReadAllBytesAsync(path);

        Assert.True(await IniFlagEditor.AddFlagAsync(path, Flag, CancellationToken.None));
        Assert.True(await IniFlagEditor.RemoveFlagAsync(path, Flag, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }
}
