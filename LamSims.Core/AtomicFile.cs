using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LamSims.Core;

/// <summary>
/// Writes text to a file so a crash mid-write cannot leave a torn or truncated file where a
/// good one belonged. The write lands beside the destination first and is flushed to disk
/// before the rename. Without that flush the temp file's bytes can still be sitting in the
/// page cache when the rename completes, and a power loss between the two leaves a
/// zero-length file at <paramref name="path"/>.
///
/// Each call writes through a temp name of its own, so two writers racing the same
/// destination degrade to last-one-wins. If they shared a name, the loser's cleanup would
/// unlink the winner's open temp file and neither write would land.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
    {
        var tempPath = $"{path}.{Path.GetRandomFileName()}.tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(contents.AsMemory(), ct);
                await writer.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// The binary counterpart of <see cref="WriteAllTextAsync"/>. Separate rather than an overload
    /// because the text member goes through a StreamWriter, which re-encodes. Anything that is not
    /// text (a PE image, an ini file whose byte-order mark must survive) needs this one.
    /// </summary>
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes,
                                                CancellationToken ct)
    {
        var tempPath = $"{path}.{Path.GetRandomFileName()}.tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            MoveIntoPlace(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// A same-volume rename, which is where the atomicity comes from. Used directly by the asset
    /// source so a verified download is written once and never copied into a second temp file.
    /// </summary>
    public static void MoveIntoPlace(string tempPath, string destination) =>
        File.Move(tempPath, destination, overwrite: true);
}
