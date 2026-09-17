using System;
using System.Collections.Generic;
using System.IO;

namespace LamSims.Core.Scanning;

/// <summary>What a directory looks like from outside. <see cref="Unreadable"/> also covers a blank
/// path, so the check is total and a caller needs no argument handling of its own.</summary>
public enum GameRootVerdict { GameRoot, SavesFolder, Unrecognised, Unreadable }

/// <summary>
/// Tells a Sims 4 installation apart from the saves directory of the same name. The two are the
/// pair users confuse, and installing into the saves directory leaves every pack invisible to the
/// game while the scan reports nothing installed.
///
/// Names are matched the way <see cref="InstallScanner"/> matches pack directories — NFC and
/// case-insensitively — because a Windows-written install is routinely read through Wine.
/// </summary>
public static class GameRootCheck
{
    /// <summary>Only ever a warning's evidence, so a layout no rule below anticipates reads
    /// <see cref="GameRootVerdict.Unrecognised"/> rather than blocking an install.</summary>
    public static GameRootVerdict Inspect(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return GameRootVerdict.Unreadable;

        var top = Children(gameDirectory);
        if (top is null) return GameRootVerdict.Unreadable;

        // Game/Bin carries the executable on every install EA ships, EA app and Steam alike, and
        // the saves directory never holds it. Checked before the saves markers so a root that also
        // holds a stray Mods directory still reads as the install.
        if (top.TryGetValue("Game", out var game) && Children(game)?.ContainsKey("Bin") == true)
            return GameRootVerdict.GameRoot;

        if (top.ContainsKey("__Installer")) return GameRootVerdict.GameRoot;

        foreach (var marker in new[] { "Mods", "Tray", "saves" })
            if (top.ContainsKey(marker)) return GameRootVerdict.SavesFolder;

        return GameRootVerdict.Unrecognised;
    }

    /// <summary>Subdirectory paths by name, or null when the directory cannot be listed. Absent and
    /// unreadable answer the same, because neither can support a verdict about the layout.</summary>
    private static Dictionary<string, string>? Children(string directory)
    {
        var children = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
                children[PathIdentity.Normalize(Path.GetFileName(child)!)] = child;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }

        return children;
    }
}
