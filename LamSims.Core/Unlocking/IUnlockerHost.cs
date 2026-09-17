using System;
using System.Collections.Generic;

namespace LamSims.Core.Unlocking;

public enum ClientRegistryKey { EaDesktop, EaDesktopWow6432, OriginWow6432, Origin }

/// <summary>
/// How the autostart value was stored. It has to survive the round trip: a REG_EXPAND_SZ read
/// expanded and written back as plain text rewrites the user's "%ProgramFiles%\..." into one
/// machine's answer to it, and anything that is not text at all cannot be written back as text.
/// </summary>
public enum AutostartValueKind { String, ExpandString, Unsupported }

/// <param name="Value">Unexpanded, exactly as stored.</param>
public sealed record AutostartValue(string Value, AutostartValueKind Kind);

/// <summary>
/// Every operating-system facility the unlocker needs that is not the filesystem. The filesystem is
/// deliberately absent: Core uses File and Directory directly throughout, and its tests drive real
/// temp directories rather than a fake.
/// </summary>
public interface IUnlockerHost
{
    /// <summary>False wherever none of this can work. Backends report IsSupported from it, so a
    /// non-Windows run detects no targets rather than throwing.</summary>
    bool IsAvailable { get; }

    bool IsElevated { get; }

    /// <summary>The ClientPath value, or null when the key or the value is absent. Must not throw
    /// for a missing key: detection walks every candidate key in order, and a machine with one
    /// client installed misses most of them.</summary>
    string? ReadClientPath(ClientRegistryKey key);

    /// <summary>The value with its kind, or null when absent. Non-null for a value of ANY type,
    /// because the entry is deleted type-blind and reporting only text values would leave a
    /// REG_DWORD entry in place and the client still starting at login.</summary>
    AutostartValue? ReadAutostartValue(string name);

    void WriteAutostartValue(string name, AutostartValue value);
    void RemoveAutostartValue(string name);

    /// <summary>Which of these process names are running. The NAMES are passed in rather than
    /// derived from a ClientKind here, so the exact-name set lives in the backend where a test can
    /// assert it. Matching case-insensitively on a prefix such as "EA" would reach unrelated
    /// software.</summary>
    IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames);

    /// <summary>Terminates them and returns the names still running when it gave up. Returning the
    /// survivors rather than void is what lets the install refuse to touch a locked directory
    /// instead of failing obscurely two steps later.</summary>
    IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                              TimeSpan perProcessTimeout);

    /// <summary>Deletes the named scheduled task; a no-op when absent. There is no create
    /// counterpart: nothing here ever registers one.</summary>
    void DeleteScheduledTask(string name);

    /// <summary>Relaunches this executable elevated. True when the new process started; false means
    /// the user declined the prompt, and nothing has changed.</summary>
    bool TryRelaunchElevated();
}
