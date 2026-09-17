using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

namespace LamSims.Core.Unlocking;

/// <summary>
/// The unlocker's two configuration files. They are treated differently on purpose: the DLC list is
/// shipped data that must be refreshed so newly listed packs are unlocked, while config.ini holds
/// options the user edits and is written only when it is absent.
/// </summary>
public static class UnlockerConfigWriter
{
    private const string ConfigResource = "LamSims.Core.Unlocking.Resources.config.ini";
    private const string DlcListResource = "LamSims.Core.Unlocking.Resources.dlc-list.ini";

    public static string ReadDefaultConfig() => Read(ConfigResource);
    public static string ReadDlcList() => Read(DlcListResource);

    public static async Task WriteAsync(UnlockerPaths paths, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);

        await AtomicFile.WriteAllTextAsync(paths.DlcListFile, ReadDlcList(), ct);

        if (!File.Exists(paths.ConfigFile))
            await AtomicFile.WriteAllTextAsync(paths.ConfigFile, ReadDefaultConfig(), ct);
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' is missing. Check the EmbeddedResource LogicalName in " +
                "LamSims.Core.csproj; the DLC list's filename contains spaces.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
