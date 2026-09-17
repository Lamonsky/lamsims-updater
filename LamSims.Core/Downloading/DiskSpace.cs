using System.IO;

namespace LamSims.Core.Downloading;

public sealed class InsufficientDiskSpaceException : IOException
{
    public InsufficientDiskSpaceException(string path, long requiredBytes, long availableBytes)
        : base($"Need {requiredBytes:N0} bytes in '{path}' but only {availableBytes:N0} are free.")
    {
        Path = path;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    public string Path { get; }
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
}

public static class DiskSpace
{
    /// <summary>
    /// Free bytes on the volume holding <paramref name="path"/>. The path need not
    /// exist yet; the nearest existing ancestor is measured instead.
    /// </summary>
    public static long GetAvailableBytes(string path)
    {
        var probe = Path.GetFullPath(path);
        while (!Directory.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || parent == probe)
                throw new DirectoryNotFoundException($"No existing ancestor of '{path}' was found.");
            probe = parent;
        }

        // DriveInfo is given the directory itself, never Path.GetPathRoot(...): on Unix
        // every absolute path roots at "/", so the root would report the OS filesystem
        // rather than the mount actually holding the downloads.
        return new DriveInfo(probe).AvailableFreeSpace;
    }

    public static void EnsureAvailable(string path, long requiredBytes)
    {
        var available = GetAvailableBytes(path);
        if (available < requiredBytes)
            throw new InsufficientDiskSpaceException(path, requiredBytes, available);
    }
}
