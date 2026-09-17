using System;
using System.IO;

namespace LamSims.Core.Unlocking;

/// <summary>
/// The two special-folder roots the unlocker writes under, injected rather than read from
/// Environment.GetFolderPath, so the whole install runs against temp directories in tests. Mirrors
/// AppPaths' optional-override constructor, including its DoNotVerify: the default option answers
/// with an empty string for a directory that does not exist yet, which would make every path below
/// relative to the process's working directory.
/// </summary>
public sealed class UnlockerPaths
{
    public UnlockerPaths(string? roamingOverride = null, string? commonAppDataOverride = null)
    {
        Roaming = roamingOverride
            ?? Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
        CommonAppData = commonAppDataOverride
            ?? Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
    }

    public string Roaming { get; }
    public string CommonAppData { get; }

    public string ConfigDirectory => Path.Combine(Roaming, "anadius", "EA DLC Unlocker v2");
    public string ConfigFile => Path.Combine(ConfigDirectory, "config.ini");
    public string DlcListFile => Path.Combine(ConfigDirectory, "g_The Sims 4.ini");
    public string MachineIniFile => Path.Combine(CommonAppData, "EA Desktop", "machine.ini");
}
