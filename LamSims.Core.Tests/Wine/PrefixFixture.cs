using System;
using System.Collections.Generic;
using System.IO;
using LamSims.Core.Unlocking;
using LamSims.Core.Logging;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

/// <summary>
/// A fake Wine prefix on the real filesystem: drive_c, the two .reg files with the header shape a
/// real prefix has, and whatever users, drives and clients a test adds. No Wine is involved and
/// no test in this file starts a process.
/// </summary>
public sealed class PrefixFixture : IDisposable
{
    public TempDir Dir { get; } = new();
    public string Root { get; }
    public string DriveC { get; }

    private readonly string _user;

    public PrefixFixture(string arch = "win64", string user = "playday", bool proton = false)
    {
        Root = Path.Combine(Dir.Path, "prefix");
        DriveC = Path.Combine(Root, "drive_c");
        _user = user;

        Directory.CreateDirectory(DriveC);
        Directory.CreateDirectory(Path.Combine(Root, "dosdevices"));

        // The header shape of a real prefix. `#arch=` sits on the fourth line, which
        // is why the reader caps its scan rather than reading 38 000 lines.
        WriteSystemReg($"WINE REGISTRY Version 2\n;; All keys relative to \\\\REGISTRY\\\\Machine\n\n#arch={arch}\n");
        WriteUserReg($"WINE REGISTRY Version 2\n;; All keys relative to \\\\REGISTRY\\\\User\\\\S-1-5-21-0\n\n#arch={arch}\n");

        AddUser("Public");
        AddUser(user);

        if (proton) ProtonMarker();
    }

    public void WriteSystemReg(string contents) =>
        File.WriteAllText(Path.Combine(Root, "system.reg"), contents);

    public void WriteUserReg(string contents) =>
        File.WriteAllText(Path.Combine(Root, "user.reg"), contents);

    public string AddUser(string name)
    {
        var directory = Path.Combine(DriveC, "users", name);
        Directory.CreateDirectory(Path.Combine(directory, "AppData", "Roaming"));
        return directory;
    }

    /// <summary>
    /// Creates the client's directory under drive_c and returns its Linux path. The windows path is
    /// given the way a real registry value carries it, backslashes and all.
    /// </summary>
    public string AddClient(ClientKind kind, string windowsPath)
    {
        var relative = windowsPath[3..].Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.Combine(DriveC, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0x4D, 0x5A]);
        return Path.GetDirectoryName(full)!;
    }

    /// <summary>
    /// A drive entry as a real DIRECTORY rather than a symlink, so the dosdevices reader is
    /// exercised on the Windows and macOS CI legs too: without it the whole mechanism is uncovered
    /// off Linux.
    /// </summary>
    public void RealDrive(char letter, string target)
    {
        Directory.CreateDirectory(target);
        var entry = Path.Combine(Root, "dosdevices", $"{letter}:");
        Directory.CreateDirectory(entry);
        foreach (var child in Directory.GetFileSystemEntries(target))
        {
            var destination = Path.Combine(entry, Path.GetFileName(child));
            if (Directory.Exists(child)) Directory.CreateDirectory(destination);
            else File.Copy(child, destination);
        }
    }

    /// <summary>A drive entry as a symlink, the way a real prefix has it. Callers gate on Windows.</summary>
    public void LinkDrive(char letter, string target)
    {
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(Root, "dosdevices", $"{letter}:"), target);
    }

    /// <summary>
    /// One of the three files that recognise a Proton/umu-managed prefix. Written at
    /// the prefix root, which is where a GE-Proton or umu prefix carries it; Valve Proton's sits
    /// one level up, which the reader also checks.
    /// </summary>
    public void ProtonMarker(string name = "version", string contents = "11.0-100") =>
        File.WriteAllText(Path.Combine(Root, name), contents);

    public WinePrefix Open(ILogSink? log = null, string? userName = null) =>
        WinePrefix.TryOpen(Root, new TargetEnvironment(EnvironmentSource.Wine, Root),
                           userName ?? _user, log)
        ?? throw new InvalidOperationException($"'{Root}' did not open as a prefix.");

    public void Dispose() => Dir.Dispose();
}

/// <summary>Collects diagnostics so a test can assert the exact strings a user sees.</summary>
public sealed class Notes : IProgress<string>
{
    public List<string> Lines { get; } = [];

    public void Report(string value) => Lines.Add(value);

    public bool Any(string fragment) =>
        Lines.Exists(l => l.Contains(fragment, StringComparison.Ordinal));
}
