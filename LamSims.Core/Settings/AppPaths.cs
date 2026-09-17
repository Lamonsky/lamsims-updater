using System;
using System.IO;

namespace LamSims.Core.Settings;

/// <summary>
/// State that is not a download: settings and the cached catalog.
/// <see cref="Environment.SpecialFolder.ApplicationData"/> answers correctly on every platform.
///
/// <see cref="Environment.SpecialFolderOption.DoNotVerify"/>, because the default returns an EMPTY
/// string for a directory that does not exist yet — a Linux account with no ~/.config — and
/// Path.Combine turns that into a relative path under the working directory.
/// </summary>
public sealed class AppPaths
{
    public string Root { get; }

    public AppPaths(string? overrideRoot = null)
    {
        Root = overrideRoot ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "lamsims-updater");
    }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CatalogCacheFile => Path.Combine(Root, "catalog.cache.json");

    /// <summary>Per-pack install markers, grouped by game directory. Under our own root, because
    /// the game directory belongs to EA's installer, repair tools and mod managers, all of which
    /// remove entries they do not recognise.</summary>
    public string InstallStateDirectory => Path.Combine(Root, "installs");

    /// <summary>The autostart value removed at install time, so removal can restore it. Not in the
    /// unlocker's config directory, which removal deletes wholesale — a backup there could not
    /// survive the operation that reads it.</summary>
    public string UnlockerAutostartBackupFile => Path.Combine(Root, "unlocker-autostart.json");

    /// <summary>
    /// Where per-client unlocker install records live. Under the app's own root for the same
    /// reason as <see cref="InstallStateDirectory"/>: the client directory belongs to EA's
    /// installer and its repair tools, which remove entries they do not recognise.
    /// </summary>
    public string UnlockerInstallDirectory => Path.Combine(Root, "unlocker-installs");

    /// <summary>Wine DLL-override records, one per prefix. Separate from
    /// <see cref="UnlockerInstallDirectory"/> because the install engine rewrites that record every
    /// install and would wipe them; keyed by prefix because one "*version" entry serves every
    /// client in it.</summary>
    public string WineOverrideDirectory => Path.Combine(Root, "wine-overrides");

    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
