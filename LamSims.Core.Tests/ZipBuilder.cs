using System.IO.Compression;
using System.Text;

namespace LamSims.Core.Tests;

/// <summary>
/// Builds archives for the installer's tests, including entry names a well-behaved zip
/// writer would never produce. <see cref="ZipArchive"/> stores the name verbatim, which is
/// exactly what the zip-slip test needs.
/// </summary>
public static class ZipBuilder
{
    public static string Create(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }
}
