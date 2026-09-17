using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LamSims.Core.Logging;

namespace LamSims.Core.Unlocking.Wine;

/// <summary>
/// <see cref="IUnlockerHost"/> for one Wine prefix, so the whole install engine runs unchanged
/// inside one. Almost every member's natural implementation is wrong here; the one that matters
/// most is <see cref="RunningClientProcesses"/>, which is ALWAYS empty.
/// </summary>
public sealed class WineUnlockerHost(WinePrefix prefix, ILogSink? log = null)
    : IUnlockerHost
{
    private static readonly Dictionary<ClientRegistryKey, string> Keys = new()
    {
        [ClientRegistryKey.EaDesktop] = @"Software\Electronic Arts\EA Desktop",
        [ClientRegistryKey.EaDesktopWow6432] = @"Software\Wow6432Node\Electronic Arts\EA Desktop",
        [ClientRegistryKey.OriginWow6432] = @"Software\Wow6432Node\Origin",
        [ClientRegistryKey.Origin] = @"Software\Origin",
    };

    public bool IsAvailable =>
        File.Exists(Path.Combine(prefix.Root, "system.reg")) && Directory.Exists(prefix.DriveC);

    /// <summary>True: a prefix belongs to the user running this, so there is no privilege to
    /// acquire, and reporting false would offer a relaunch that changes nothing. A read-only mount
    /// or another uid's prefix fails at the write instead.</summary>
    public bool IsElevated => true;

    /// <summary>Whether any candidate key held a value, so the caller can tell "no client is
    /// registered" from "one is, and its path is gone" — the engine only returns a list.</summary>
    public bool SawClientValue { get; private set; }

    /// <summary>The RESOLVED Linux path, never the raw registry value: on Linux
    /// <c>Path.GetDirectoryName</c> of a Windows path returns EMPTY, so a host handing back the raw
    /// value makes detection find nothing on every prefix, with no error.</summary>
    public string? ReadClientPath(ClientRegistryKey key)
    {
        if (!Keys.TryGetValue(key, out var registryKey)) return null;

        // A client can be registered in system.reg or user.reg, and reading one misses every prefix
        // that used the other. A stale path in the first must not stop the second being checked.
        string? firstUnresolvable = null;

        foreach (var file in new[] { "system.reg", "user.reg" })
        {
            var value = WineRegistryFile.ReadValue(Path.Combine(prefix.Root, file), registryKey,
                                                   "ClientPath");
            if (value?.Text is not { Length: > 0 } text) continue;

            SawClientValue = true;

            // For a REG_EXPAND_SZ, try expanding with each user's own directory, not just the
            // primary one. A client registered under a non-primary user must expand correctly.
            var candidates = value.Expandable
                ? prefix.WindowsUserDirectories.Select(dir =>
                    WineRegistryFile.Expand(text, Path.GetFileName(dir))).ToList()
                : [text];

            foreach (var windows in candidates)
            {
                var resolved = prefix.ResolveWindowsPath(windows);
                if (resolved is not null && File.Exists(resolved)) return resolved;

                if (firstUnresolvable is null) firstUnresolvable = windows;
            }
        }

        // If nothing resolved, report once with the first unresolvable path. firstUnresolvable is
        // only ever assigned after SawClientValue is set true in this same call, so a non-null
        // value here already implies SawClientValue — an explicit check of it would be redundant.
        if (firstUnresolvable is not null)
        {
            log?.Write(LogLine.Warning(
                $"{prefix.Environment.Describe()}: the client is registered at "
                + $"'{firstUnresolvable}' but that path does not exist in this prefix."));
        }

        return null;
    }

    /// <summary>ALWAYS empty, even when a client is running. Wiring this to IWineProcesses fails
    /// every install with the client up: the gate goes non-empty, the kill returns survivors and
    /// the engine hard-fails. Under Wine unlink+rename succeeds against a mapped file, so the
    /// client scan belongs to the wrapper, as a "restart the client" warning.</summary>
    public IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames) => [];

    /// <summary>Always empty, nothing signalled: killing wineserver can lose registry state, and a
    /// shared prefix takes a running game down with it.</summary>
    public IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                                     TimeSpan perProcessTimeout) => [];

    /// <summary>Null: the Run key is inert under Wine, so there is nothing to back up.</summary>
    public AutostartValue? ReadAutostartValue(string name) => null;

    public void WriteAutostartValue(string name, AutostartValue value)
    {
        // No-op. Storing it would make removal restore a value into a registry nothing reads.
    }

    public void RemoveAutostartValue(string name)
    {
        // No-op, and specifically not a throw: the engine's autostart step is documented non-fatal
        // but runs AFTER version.dll is on disk, so a throw here fails an install that worked.
    }

    public void DeleteScheduledTask(string name)
    {
        // No-op. Windows scheduled tasks do not exist in a prefix.
    }

    /// <summary>False, and unreachable: <see cref="IsElevated"/> is always true.</summary>
    public bool TryRelaunchElevated() => false;
}
