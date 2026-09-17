using System;
using System.IO.Compression;
using LamSims.Core.Catalogs;

namespace LamSims.Core.Tests;

/// <summary>
/// The stock pack entry and archive writer shared by the installer's tests. <see cref="Pack"/>
/// always describes "EP01", and <see cref="WriteArchive"/> writes an archive of entries filled
/// with random bytes of the given sizes, for tests that care about extraction size and count
/// rather than content.
/// </summary>
internal static class ZipFixture
{
    public const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static PackEntry Pack(long? installedSize = null) => new(
        "EP01", "The Sims 4 Get to Work", PackType.Expansion, 1024, installedSize, Digest,
        new[] { new Uri("https://host-a.example.invalid/EP01.zip") }, new[] { "EP01" });

    public static string WriteArchive(TempDir dir, params (string Name, long Size)[] entries)
    {
        var path = dir.File("EP01.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, size) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(Payloads.Random(checked((int)size)));
        }

        return path;
    }
}
