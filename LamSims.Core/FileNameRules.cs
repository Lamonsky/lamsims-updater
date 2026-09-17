using System;

namespace LamSims.Core;

/// <summary>
/// Rules for a string that will name a file on disk. Enforced on every platform rather than
/// only on Windows, because the catalog is one document served to all of them and a code that
/// resolves to a device on one platform and a file on another would pass on a Linux
/// developer's machine.
/// </summary>
public static class FileNameRules
{
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// True for a Windows reserved device name, with or without an extension. Windows resolves
    /// "NUL.part" to the null device, so a write reports success and discards every byte.
    /// </summary>
    public static bool IsReservedDeviceName(string name)
    {
        var stem = name.AsSpan();
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];

        foreach (var reserved in Reserved)
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// True when Windows would silently strip the last character, making two names that differ
    /// as strings collide as paths.
    /// </summary>
    public static bool HasTrailingDotOrSpace(string name) =>
        name.Length > 0 && (name[^1] == '.' || name[^1] == ' ');

    /// <summary>
    /// Every character no platform may have in a file name, taken as the union rather than the
    /// local answer. Path.GetInvalidFileNameChars() returns two characters on Unix and thirty-odd
    /// on Windows, so validating with it accepts a catalog on one platform and rejects it on
    /// another, and a pack would install for a Linux user and silently vanish for a Windows one.
    /// </summary>
    public static bool HasInvalidCharacter(string name)
    {
        foreach (var c in name)
            if (c < ' ' || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return true;

        return false;
    }
}
