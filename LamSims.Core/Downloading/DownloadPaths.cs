using System;
using System.IO;
using LamSims.Core;

namespace LamSims.Core.Downloading;

/// <summary>
/// Resolves where partial downloads, archives and quarantined files live.
/// Deliberately not the system temp directory: on most Linux distributions /tmp is
/// tmpfs, i.e. RAM, and archives here reach several gigabytes.
/// </summary>
public sealed class DownloadPaths
{
    /// <summary>Where downloads go when nothing is configured. <see cref="Retarget"/> returns here
    /// when given nothing, so clearing the setting cannot leave the engine on the cleared
    /// directory. DoNotVerify for the reason <see cref="LamSims.Core.Settings.AppPaths"/> gives.</summary>
    public string DefaultRoot { get; }

    public string Root { get; private set; }

    public DownloadPaths(string? overrideRoot = null)
    {
        DefaultRoot = overrideRoot ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "lamsims-updater", "downloads");
        Root = DefaultRoot;
    }

    /// <summary>
    /// Moves every path this instance hands out, or back to <see cref="DefaultRoot"/> when given
    /// nothing, and creates it. Every consumer asks this one instance per call, so one call moves
    /// them all without rebuilding the object graph.
    ///
    /// <para><b>Only while nothing is running:</b> a retarget under a live download orphans the
    /// <c>.part</c> file and the lock the run still holds. Existing archives are not moved, and
    /// <see cref="Root"/> is assigned only once the directory exists, so a failure leaves the
    /// engine where it was.</para>
    /// </summary>
    public void Retarget(string? directory)
    {
        var root = string.IsNullOrWhiteSpace(directory) ? DefaultRoot : directory;
        Directory.CreateDirectory(root);
        Root = root;
    }

    public string PartFile(string code) => Path.Combine(Root, Validate(code) + ".part");
    public string StateFile(string code) => Path.Combine(Root, Validate(code) + ".part.json");
    public string ArchiveFile(string code) => Path.Combine(Root, Validate(code) + ".zip");
    public string QuarantineFile(string code) => Path.Combine(Root, Validate(code) + ".zip.bad");

    /// <summary>The verified-identity record, beside the archive itself so it follows the archive
    /// when the directory is moved or copied, and so <see cref="OrphanCleaner"/> sweeps it with
    /// everything else.</summary>
    public string ArchiveDigestFile(string code) => Path.Combine(Root, Validate(code) + ".zip.json");

    /// <summary>The cache entry for one unlocker DLL, through the same <c>Validate</c> guard as
    /// everything else: <see cref="OrphanCleaner"/> sweeps all of <see cref="Root"/>, and an
    /// escaping path would turn that sweep into a delete somewhere else.</summary>
    public string UnlockerAssetFile(string fileName) => Path.Combine(Root, Validate(fileName));

    /// <summary>
    /// The advisory lock for one pack, held for its whole run. Beside the archive so a second
    /// instance pointed at the same download directory contends for the same file.
    /// </summary>
    public string LockFile(string code) => Path.Combine(Root, Validate(code) + ".lock");

    public void EnsureCreated() => Directory.CreateDirectory(Root);

    private static string Validate(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Pack code must not be blank.", nameof(code));
        if (FileNameRules.HasInvalidCharacter(code))
            throw new ArgumentException($"Pack code '{code}' contains path characters.", nameof(code));
        // "." and ".." carry no invalid filename character, so the check above lets them through
        // and Path.Combine resolves them to Root itself or to its parent.
        if (code is "." or "..")
            throw new ArgumentException($"Pack code '{code}' is a directory reference.", nameof(code));
        if (FileNameRules.IsReservedDeviceName(code))
            throw new ArgumentException(
                $"Pack code '{code}' is a reserved device name.", nameof(code));
        if (FileNameRules.HasTrailingDotOrSpace(code))
            throw new ArgumentException(
                $"Pack code '{code}' ends with a dot or a space.", nameof(code));
        return code;
    }
}
