using System.IO;
using Xunit;
using LamSims.Core.Scanning;

namespace LamSims.Core.Tests;

public class GameRootCheckTests
{
    [Fact]
    public void Reads_a_directory_holding_Game_Bin_as_an_installation()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Game", "Bin"));

        Assert.Equal(GameRootVerdict.GameRoot, GameRootCheck.Inspect(temp.Path));
    }

    [Fact]
    public void Reads_Game_Bin_whatever_case_the_filesystem_returns()
    {
        // A Windows-written installation read through Wine, or one restored by a tool that
        // lowercased its names: on a case-sensitive filesystem an ordinal match would miss it and
        // warn about a directory that really does hold the game.
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "game", "bin"));

        Assert.Equal(GameRootVerdict.GameRoot, GameRootCheck.Inspect(temp.Path));
    }

    [Fact]
    public void Reads_a_directory_holding_the_installer_as_an_installation()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "__Installer"));

        Assert.Equal(GameRootVerdict.GameRoot, GameRootCheck.Inspect(temp.Path));
    }

    [Fact]
    public void Reads_the_saves_folder_as_the_saves_folder()
    {
        // Documents/Electronic Arts/The Sims 4 — same name as the installation, and the directory
        // users pick by mistake.
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Mods"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "Tray"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "saves"));

        Assert.Equal(GameRootVerdict.SavesFolder, GameRootCheck.Inspect(temp.Path));
    }

    [Fact]
    public void Prefers_the_installation_over_the_saves_folder_when_a_root_holds_both()
    {
        // Nothing stops a user keeping mods beside the game, and calling that the saves folder
        // would warn about the one directory that is right.
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Game", "Bin"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "Mods"));

        Assert.Equal(GameRootVerdict.GameRoot, GameRootCheck.Inspect(temp.Path));
    }

    [Fact]
    public void Reads_a_directory_matching_neither_layout_as_unrecognised()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Downloads"));

        Assert.Equal(GameRootVerdict.Unrecognised, GameRootCheck.Inspect(temp.Path));
    }

    [Fact]
    public void Reads_a_Game_directory_with_no_Bin_inside_it_as_unrecognised()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Game"));

        Assert.Equal(GameRootVerdict.Unrecognised, GameRootCheck.Inspect(temp.Path));
    }

    [Fact]
    public void Reads_an_absent_directory_as_unreadable()
    {
        using var temp = new TempDir();

        Assert.Equal(GameRootVerdict.Unreadable,
                     GameRootCheck.Inspect(Path.Combine(temp.Path, "no-such-directory")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reads_a_blank_path_as_unreadable(string? path) =>
        Assert.Equal(GameRootVerdict.Unreadable, GameRootCheck.Inspect(path));
}
