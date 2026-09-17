using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace LamSims.Core.Downloading;

/// <summary>
/// Hashes a completed archive in one sequential pass. SHA-256 is inherently sequential,
/// so a parallel download cannot produce the digest as it goes.
/// </summary>
public static class Sha256Verifier
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> ComputeAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        var digest = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
