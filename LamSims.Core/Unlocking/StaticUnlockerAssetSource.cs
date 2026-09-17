using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Downloading;
using LamSims.Core.Logging;

namespace LamSims.Core.Unlocking;

/// <param name="Url">The upstream project's own release asset.</param>
/// <param name="Size">Checked against Content-Length before any bytes transfer.</param>
/// <param name="Sha256">Lowercase hex.</param>
/// <param name="FileName">The cache entry's name under the download directory.</param>
public sealed record AssetPin(string Url, long Size, string Sha256, string FileName);

/// <summary>
/// The two unlocker DLLs, pinned by digest. The URLs point at a MUTABLE release tag, so an upstream
/// re-upload stops this build installing until the pins are updated. The DLL is loaded into EA
/// Desktop's process, so an unnoticed substitution is worth failing on, and the error names the
/// cause so an operator can tell a rotation from corruption.
/// </summary>
public sealed class StaticUnlockerAssetSource(
    HttpClient http,
    DownloadPaths paths,
    IReadOnlyDictionary<ClientKind, AssetPin>? pins = null,
    RetryOptions? retry = null,
    ILogSink? log = null) : IUnlockerAssetSource
{
    private const string Base = "https://github.com/Lamonsky/lamsims-updater/releases/download/Beta/";

    private readonly ILogSink _log = log ?? NullLogSink.Instance;

    /// <summary>The shipped pin table, injected so tests can point at the local server instead of
    /// the public internet.</summary>
    public static IReadOnlyDictionary<ClientKind, AssetPin> ShippedPins { get; } =
        new Dictionary<ClientKind, AssetPin>
        {
            // EA Desktop is 64-bit and Origin is 32-bit, so these are not interchangeable: the EA
            // app DLL is PE32+ x86-64 and the Origin one is PE32 i386. A swapped pair would fail
            // silently at load time rather than loudly at install time.
            [ClientKind.EaApp] = new(Base + "ea_app_version.dll", 245248,
                "70553a2b4d53eddf1eeb290e346a9b562c71f52d46874bfb708e9d962469a736",
                "ea_app_version.dll"),
            [ClientKind.Origin] = new(Base + "origin_version.dll", 192512,
                "cf784476719a93e3fb8457a2d4c4580b691b6d04592b9a4467acf563f30d2b83",
                "origin_version.dll"),
        };

    private readonly IReadOnlyDictionary<ClientKind, AssetPin> _pins = pins ?? ShippedPins;
    private readonly RetryOptions _retry = retry ?? RetryOptions.Default;

    /// <summary>Removes a temp file, reporting nothing, so a refused delete cannot replace the
    /// diagnosis the caller is owed or invent one where the call succeeded.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task<ReadOnlyMemory<byte>> GetDllAsync(ClientKind client, CancellationToken ct)
    {
        var pin = _pins[client];
        var cached = paths.UnlockerAssetFile(pin.FileName);

        if (File.Exists(cached))
        {
            try
            {
                // Hashed over the bytes this returns, not over the file it read them from: the
                // caller installs these bytes, so the check and the payload must be the same
                // object.
                var reused = await File.ReadAllBytesAsync(cached, ct);
                if (Matches(reused, pin.Sha256))
                {
                    _log.Write(LogLine.Info("Reusing the cached DLL"));
                    return reused;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Falls through to the fetch, like a digest that does not match: an entry that
                // cannot be read is no reason to fail an install that has a URL to fetch from, and
                // the fetch replaces the entry it could not read. On Windows a concurrent fetch
                // replacing this path is enough to produce this.
            }
        }

        _log.Write(LogLine.Info($"Fetching the unlocker DLL for {client}"));

        paths.EnsureCreated();
        var temp = $"{cached}.{Path.GetRandomFileName()}.incoming";

        try
        {
            // The header deadline is not optional: HttpFactory sets Timeout.InfiniteTimeSpan
            // because the pack path bounds itself per operation, so without this the fetch has no
            // timeout at all and a server that stalls after accepting hangs the install.
            using var request = new HttpRequestMessage(HttpMethod.Get, pin.Url);

            // Four arguments: HttpDeadline.SendAsync already passes ResponseHeadersRead itself.
            using var response = await HttpDeadline.SendAsync(http, request, _retry.HeaderTimeout, ct);
            response.EnsureSuccessStatusCode();

            var advertised = response.Content.Headers.ContentLength;
            if (advertised is not null && advertised != pin.Size)
                throw new SizeMismatchException(pin.Size, advertised.Value);

            await using (var incoming = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                                  FileShare.None))
            {
                // Each READ is bounded, not the whole transfer: a server that sends its headers and
                // then stops sending bytes must fail rather than hang the install. CopyToAsync would
                // wait forever, because HttpFactory's client has Timeout.InfiniteTimeSpan.
                var buffer = new byte[81920];
                var received = 0L;
                while (true)
                {
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readDeadline.CancelAfter(_retry.ReadTimeout);

                    int read;
                    try
                    {
                        read = await incoming.ReadAsync(buffer, readDeadline.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException(
                            $"The DLL at '{pin.Url}' stopped sending data mid-transfer.");
                    }

                    if (read == 0) break;

                    // A response without Content-Length skips the size gate above, and the payload
                    // is unauthenticated until the digest check. Without this a server that never
                    // stops sending fills the volume the pack queue is downloading into.
                    received += read;
                    if (received > pin.Size)
                        throw new IOException(
                            $"The DLL at '{pin.Url}' sent more than the expected {pin.Size} bytes.");

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                // Flushed to disk before the rename, mirroring AtomicFile.WriteAllBytesAsync:
                // without this the temp file's bytes can still be sitting in the page cache when
                // the rename completes, and a power loss between the two can leave a truncated
                // file at the pinned cache path.
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }

            // Read and hashed before the rename, and these are the bytes returned: the caller
            // installs exactly what was verified here, and nothing after this reads the destination
            // back. That read is what a concurrent fetch for the same client cannot survive on
            // Windows, where it collides with the other fetch's replace of that path
            // (ERROR_SHARING_VIOLATION) as surely as the replace collides with it. The transfer
            // above is bounded by pin.Size, so this is bounded by it too.
            var bytes = await File.ReadAllBytesAsync(temp, ct);
            if (!Matches(bytes, pin.Sha256))
                throw new UnlockerAssetMismatchException(pin.Url, pin.Sha256, Digest(bytes));

            // A rename, not a copy: the payload is written to disk exactly once and no orphan is
            // left behind.
            //
            // Best effort, and deliberately: the cache is what makes the NEXT call cheap, and the
            // bytes this call owes its caller are already verified and in hand. Replacing a file
            // some other handle holds open is refused on Windows, where MoveFileEx answers
            // ERROR_ACCESS_DENIED, and a concurrent reader taking the cache-reuse path above is
            // enough to hold it; a read-only directory and a full volume refuse it too. Failing
            // the install over any of those would be failing it because an optimisation could not
            // be written.
            try
            {
                AtomicFile.MoveIntoPlace(temp, cached);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
            }

            return bytes;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, string expected) =>
        string.Equals(Digest(bytes), expected, StringComparison.OrdinalIgnoreCase);

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
