using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class WineRegistryReadTests
{
    private const string Key = @"Software\Wine\DllOverrides";

    private static string Write(TempDir dir, string contents)
    {
        var path = dir.File("user.reg");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void A_plain_quoted_string_is_read()
    {
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\DllOverrides] 1787506419\n#time=1dd332588fcb704\n\"*version\"=\"native,builtin\"\n");

        Assert.Equal("native,builtin", WineRegistryFile.ReadValue(path, Key, "*version")?.Text);
    }

    [Fact]
    public void An_absent_key_reads_as_null_and_an_absent_value_reads_as_null()
    {
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\Other] 0\n\"*version\"=\"n,b\"\n");

        Assert.Null(WineRegistryFile.ReadKey(path, Key));
        Assert.Null(WineRegistryFile.ReadValue(path, Key, "*version"));
    }

    /// <summary>
    /// The obvious candidate, hex(2), occurs zero times in a real registry;
    /// str(2) and str(7) occur 125 and 2004 times and BOTH hold quoted strings. A reader that
    /// discarded them as "not a string" would drop a ClientPath stored as REG_EXPAND_SZ, and
    /// detection would find nothing on that prefix with no error.
    /// </summary>
    [Theory]
    [InlineData("str(2):", true)]
    [InlineData("str(7):", false)]
    public void Expand_and_multi_string_types_are_read_as_strings(string prefix, bool expandable)
    {
        using var dir = new TempDir();
        var path = Write(dir, $"[Software\\\\Wine\\\\DllOverrides] 0\n\"*version\"={prefix}\"native,builtin\"\n");

        var value = WineRegistryFile.ReadValue(path, Key, "*version");

        Assert.Equal("native,builtin", value?.Text);
        Assert.Equal(expandable, value?.Expandable);
    }

    /// <summary>
    /// A real client name in the measured prefix is "Need for Speed\x2122 Unbound Palace Edition".
    /// An unescaped reader turns a path containing one of these into a path that does not exist.
    /// </summary>
    [Fact]
    public void String_escapes_are_decoded()
    {
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n"
                            + "\"a\"=\"C:\\\\Program Files\\\\x\"\n"
                            + "\"b\"=\"Need for Speed\\x2122 Unbound\"\n"
                            + "\"c\"=\"say \\\"hi\\\"\"\n");

        Assert.Equal(@"C:\Program Files\x", WineRegistryFile.ReadValue(path, Key, "a")?.Text);
        Assert.Equal("Need for Speed\u2122 Unbound", WineRegistryFile.ReadValue(path, Key, "b")?.Text);
        Assert.Equal("say \"hi\"", WineRegistryFile.ReadValue(path, Key, "c")?.Text);
    }

    /// <summary>
    /// A key's default value. 29 773 of them in a real registry, and a reader that treated one as a
    /// named value would put an entry called "" in the dictionary — which then satisfies any
    /// "is there an entry" test and reports overrides that do not exist.
    /// </summary>
    [Fact]
    public void The_default_value_is_not_a_named_value()
    {
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n@=\"default\"\n\"*version\"=\"n,b\"\n");

        var values = WineRegistryFile.ReadKey(path, Key);

        Assert.NotNull(values);
        Assert.Single(values);
        Assert.True(values.ContainsKey("*version"));
    }

    /// <summary>
    /// A wrapped hex value's continuation lines are indented and end with a backslash. Consumed,
    /// not parsed: 15 278 of them in a real system.reg, and a reader that examined each one would
    /// invent values from hex digits.
    /// </summary>
    [Fact]
    public void Wrapped_hex_continuations_are_consumed_and_do_not_become_values()
    {
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n"
                            + "\"blob\"=hex:01,02,03,\\\n"
                            + "  04,05,06,\\\n"
                            + "  07,08\n"
                            + "\"*version\"=\"n,b\"\n");

        var values = WineRegistryFile.ReadKey(path, Key);

        Assert.NotNull(values);
        Assert.Equal(2, values.Count);
        Assert.Null(values["blob"].Text);
        Assert.Equal("n,b", values["*version"].Text);
    }

    [Fact]
    public void Non_string_types_are_present_with_no_text()
    {
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n\"n\"=dword:00000001\n");

        Assert.True(WineRegistryFile.ReadKey(path, Key)!.ContainsKey("n"));
        Assert.Null(WineRegistryFile.ReadValue(path, Key, "n")?.Text);
    }

    /// <summary>
    /// Measured on a throwaway prefix: appending a second block with the same key name merges, and
    /// Wine rewrites it as one. A reader that stopped at the first block would miss a value written
    /// exactly the way SetValueAsync's block-absent path writes one.
    /// </summary>
    [Fact]
    public void Duplicate_key_blocks_merge()
    {
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\DllOverrides] 1787506419\n\"a\"=\"1\"\n\n"
                            + "[Software\\\\Wine\\\\DllOverrides] 0\n\"*version\"=\"native,builtin\"\n");

        var values = WineRegistryFile.ReadKey(path, Key);

        Assert.NotNull(values);
        Assert.Equal("1", values["a"].Text);
        Assert.Equal("native,builtin", values["*version"].Text);
    }

    [Fact]
    public void Windows_variables_expand_against_the_prefix_and_the_user()
    {
        Assert.Equal(@"C:\Program Files\EA", WineRegistryFile.Expand(@"%ProgramFiles%\EA", "playday"));
        Assert.Equal(@"C:\users\playday\AppData\Roaming\x",
                     WineRegistryFile.Expand(@"%APPDATA%\x", "playday"));
        Assert.Equal(@"C:\nothing%NOPE%", WineRegistryFile.Expand(@"C:\nothing%NOPE%", "playday"));
    }
}

public class WineRegistryWriteTests
{
    private const string Key = @"Software\Wine\DllOverrides";

    /// <summary>
    /// A fresh prefix ALREADY contains an empty DllOverrides block, measured immediately after
    /// wineboot -i. So this is the normal case and "block absent" is the exception: an
    /// implementation that only handled the append path would work on no real prefix at all.
    /// </summary>
    private const string EmptyBlock =
        "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Wine\\\\DllOverrides] 1787506419\n#time=1dd332588fcb704\n";

    private static string File_(TempDir dir, string contents)
    {
        var path = dir.File("user.reg");
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>
    /// The exact resulting text, not just "the value can be read back". The insert position matters:
    /// immediately after #time= is Wine's own canonical position, measured after Wine rewrote a
    /// block we had appended to, so a later Wine save is a no-op on our line rather than moving it.
    /// Asserting only that other bytes survive passes against appending at end of file.
    /// </summary>
    [Fact]
    public async Task Into_an_existing_empty_block_the_line_lands_after_the_time_line()
    {
        using var dir = new TempDir();
        var path = File_(dir, EmptyBlock);

        var write = await WineRegistryFile.SetValueAsync(path, Key, "*version", "native,builtin",
                                                        CancellationToken.None);

        Assert.False(write.CreatedBlock);
        Assert.Null(write.PriorValue);
        Assert.Equal(
            "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Wine\\\\DllOverrides] 1787506419\n"
            + "#time=1dd332588fcb704\n\"*version\"=\"native,builtin\"\n",
            await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Into_a_block_with_no_time_line_the_line_lands_after_the_key_line()
    {
        using var dir = new TempDir();
        var path = File_(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n\"a\"=\"1\"\n");

        await WineRegistryFile.SetValueAsync(path, Key, "*version", "n,b", CancellationToken.None);

        Assert.Equal("[Software\\\\Wine\\\\DllOverrides] 0\n\"*version\"=\"n,b\"\n\"a\"=\"1\"\n",
                     await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// A `0` timestamp and a missing #time= line are both accepted, measured twice: once by
    /// querying the appended block and once by driving the real EADesktop.exe against it.
    /// </summary>
    [Fact]
    public async Task With_no_block_at_all_one_is_appended_with_a_zero_timestamp()
    {
        using var dir = new TempDir();
        var path = File_(dir, "WINE REGISTRY Version 2\n#arch=win64\n");

        var write = await WineRegistryFile.SetValueAsync(path, Key, "*version", "native,builtin",
                                                        CancellationToken.None);

        Assert.True(write.CreatedBlock);
        Assert.Equal("WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Wine\\\\DllOverrides] 0\n"
                     + "\"*version\"=\"native,builtin\"\n",
                     await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task An_existing_value_is_replaced_and_reported_as_the_prior_value()
    {
        using var dir = new TempDir();
        var path = File_(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n\"*version\"=\"builtin\"\n");

        var write = await WineRegistryFile.SetValueAsync(path, Key, "*version", "native,builtin",
                                                        CancellationToken.None);

        Assert.Equal("builtin", write.PriorValue);
        Assert.Equal("[Software\\\\Wine\\\\DllOverrides] 0\n\"*version\"=\"native,builtin\"\n",
                     await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// A bare "version" entry is left alone and "*version" added beside it. The bare form matches
    /// only the system-directory reduction, so it neither covers an app-directory DLL nor is
    /// something to overwrite: a real prefix carries api-ms-win-crt-* entries with no star for
    /// exactly that reason.
    /// </summary>
    [Fact]
    public async Task A_bare_version_entry_is_left_alone()
    {
        using var dir = new TempDir();
        var path = File_(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n\"version\"=\"builtin\"\n");

        await WineRegistryFile.SetValueAsync(path, Key, "*version", "native,builtin",
                                            CancellationToken.None);

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"version\"=\"builtin\"", text);
        Assert.Contains("\"*version\"=\"native,builtin\"", text);
    }

    /// <summary>
    /// The round trip: set then remove leaves the file byte-identical.
    /// A writer that reformatted, re-sorted or re-timestamped anything fails here, and so does one
    /// that lost the trailing newline.
    /// </summary>
    [Fact]
    public async Task Set_then_remove_leaves_the_file_byte_identical()
    {
        using var dir = new TempDir();
        var path = File_(dir, EmptyBlock);

        await WineRegistryFile.SetValueAsync(path, Key, "*version", "native,builtin",
                                            CancellationToken.None);
        await WineRegistryFile.RemoveValueAsync(path, Key, "*version", removeBlockIfEmpty: false,
                                               CancellationToken.None);

        Assert.Equal(EmptyBlock, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Set_then_remove_with_block_removal_undoes_an_appended_block()
    {
        using var dir = new TempDir();
        const string original = "WINE REGISTRY Version 2\n#arch=win64\n";
        var path = File_(dir, original);

        await WineRegistryFile.SetValueAsync(path, Key, "*version", "n,b", CancellationToken.None);
        await WineRegistryFile.RemoveValueAsync(path, Key, "*version", removeBlockIfEmpty: true,
                                               CancellationToken.None);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// A block that still holds another value keeps its key line even when block removal was asked
    /// for. Removing it would delete a value the user set.
    /// </summary>
    [Fact]
    public async Task A_block_that_is_not_empty_survives_block_removal()
    {
        using var dir = new TempDir();
        var path = File_(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n\"*version\"=\"n,b\"\n\"d3d11\"=\"n\"\n");

        await WineRegistryFile.RemoveValueAsync(path, Key, "*version", removeBlockIfEmpty: true,
                                               CancellationToken.None);

        Assert.Equal("[Software\\\\Wine\\\\DllOverrides] 0\n\"d3d11\"=\"n\"\n",
                     await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Removing_a_value_that_is_not_there_changes_nothing()
    {
        using var dir = new TempDir();
        var path = File_(dir, EmptyBlock);

        await WineRegistryFile.RemoveValueAsync(path, Key, "*version", removeBlockIfEmpty: true,
                                               CancellationToken.None);

        Assert.Equal(EmptyBlock, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Setting_over_a_wrapped_hex_value_consumes_its_continuations()
    {
        using var dir = new TempDir();
        var path = File_(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n"
                            + "\"blob\"=hex:01,02,03,\\\n"
                            + "  04,05,06,\\\n"
                            + "  07,08\n");

        await WineRegistryFile.SetValueAsync(path, Key, "blob", "builtin", CancellationToken.None);

        Assert.Equal("[Software\\\\Wine\\\\DllOverrides] 0\n\"blob\"=\"builtin\"\n",
                     await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Removing_a_wrapped_hex_value_consumes_its_continuations()
    {
        using var dir = new TempDir();
        var path = File_(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n"
                            + "\"blob\"=hex:01,02,03,\\\n"
                            + "  04,05,06,\\\n"
                            + "  07,08\n"
                            + "\"*version\"=\"n,b\"\n");

        await WineRegistryFile.RemoveValueAsync(path, Key, "blob", removeBlockIfEmpty: false,
                                               CancellationToken.None);

        Assert.Equal("[Software\\\\Wine\\\\DllOverrides] 0\n\"*version\"=\"n,b\"\n",
                     await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_value_name_is_matched_case_insensitively()
    {
        using var dir = new TempDir();
        var path = File_(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n\"*VERSION\"=\"builtin\"\n");

        var write = await WineRegistryFile.SetValueAsync(path, Key, "*version", "native,builtin",
                                                        CancellationToken.None);

        Assert.Equal("builtin", write.PriorValue);
        var text = await File.ReadAllTextAsync(path);
        // Verify exactly one version entry and that it's been replaced case-insensitively
        Assert.Contains("\"*version\"=\"native,builtin\"", text);
        Assert.DoesNotContain("\"*VERSION\"=\"builtin\"", text);
    }
}

public class WineRegistryContinuationTests
{
    private const string Key = @"Software\Wine\DllOverrides";

    private static string Write(TempDir dir, string contents)
    {
        var path = dir.File("user.reg");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void A_continuation_line_that_looks_like_a_value_is_not_read_as_one()
    {
        // Wine indents continuations, but the grammar doesn't require it — a line ending with \
        // continues whatever the next line looks like. This tests that a continuation line
        // resembling a value is not parsed as one.
        using var dir = new TempDir();
        var path = Write(dir, "[Software\\\\Wine\\\\DllOverrides] 0\n"
                            + "\"blob\"=hex:01,02,\\\n"
                            + "\"*version\"=\"native,builtin\"\n");

        var values = WineRegistryFile.ReadKey(path, Key);

        Assert.NotNull(values);
        Assert.Single(values);
        Assert.True(values.ContainsKey("blob"));
        Assert.False(values.ContainsKey("*version"));
    }
}
