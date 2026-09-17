using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Security.Cryptography;
using LamSims.Core.Downloading;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class StaticUnlockerAssetSourceTests
{
    private static byte[] Payload() => Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (StaticUnlockerAssetSource Source, DownloadPaths Paths) Build(
        TestFileServer server, TempDir dir, byte[] payload, string? digestOverride = null,
        long? sizeOverride = null, RetryOptions? retry = null)
    {
        var paths = new DownloadPaths(dir.Path);
        var pins = new Dictionary<ClientKind, AssetPin>
        {
            [ClientKind.EaApp] = new(server.FileUrl.ToString(), sizeOverride ?? payload.Length,
                                     digestOverride ?? Digest(payload), "ea_app_version.dll"),
        };
        return (new StaticUnlockerAssetSource(new HttpClient(), paths, pins, retry), paths);
    }

    [Fact]
    public async Task A_matching_digest_returns_the_cached_path()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload);

        var bytes = await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        // The cache write is checked too, since the next run's reuse depends on the pinned name.
        Assert.Equal(payload, bytes.ToArray());
        Assert.Equal(payload,
            await File.ReadAllBytesAsync(paths.UnlockerAssetFile("ea_app_version.dll")));
    }

    // A throw alone would pass against an implementation that had already written the bad bytes.
    [Fact]
    public async Task A_mismatched_digest_throws_and_caches_nothing()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload, digestOverride: new string('a', 64));

        var ex = await Assert.ThrowsAsync<UnlockerAssetMismatchException>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Equal(new string('a', 64), ex.Expected);
        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    /// <summary>
    /// The pair: it throws AND nothing is cached. Without a Content-Length the advertised-size
    /// gate cannot fire, so the pinned size is the only thing bounding the write. The payload is
    /// unauthenticated until the digest check, and the volume being written to is the one the
    /// pack queue downloads into.
    /// </summary>
    [Fact]
    public async Task A_response_with_no_content_length_cannot_write_past_the_pinned_size()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(
            payload, new TestFileServerOptions { OmitContentLength = true });

        // The digest still matches the payload, so only the size cap can stop this.
        var (source, paths) = Build(server, dir, payload, sizeOverride: 1024);

        var ex = await Assert.ThrowsAsync<IOException>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Contains("more than the expected 1024 bytes", ex.Message);
        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    // A re-fetch reproduces the file's mtime and bytes, so only the request count distinguishes
    // a cache hit from no cache at all.
    [Fact]
    public async Task A_cached_file_with_the_right_digest_is_not_fetched_again()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, _) = Build(server, dir, payload);

        await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);
        var afterFirst = server.RequestCount;

        await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.Equal(afterFirst, server.RequestCount);
    }

    // A size disagreeing with the pin must fail before transferring, as the pack path does with
    // catalog sizes.
    [Fact]
    public async Task A_size_disagreeing_with_the_pin_fails_without_caching()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload, sizeOverride: payload.Length + 1);

        await Assert.ThrowsAnyAsync<Exception>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    /// <summary>
    /// HttpFactory sets Timeout.InfiniteTimeSpan, so without an explicit header deadline this fetch
    /// inherits no timeout at all and a stalling server hangs the install forever.
    /// </summary>
    [Fact]
    public async Task A_server_that_stalls_before_headers_fails_rather_than_hanging()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload, new()
        {
            StallBeforeHeaders = TimeSpan.FromSeconds(30),
        });
        var (source, _) = Build(server, dir, payload,
            retry: RetryOptions.Default with { HeaderTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAnyAsync<Exception>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));
    }

    /// <summary>
    /// Headers arrive and then the body stops, which is the case CopyToAsync does not bound.
    /// HttpFactory's client is built with Timeout.InfiniteTimeSpan, so without a per-read bound
    /// this hangs.
    /// </summary>
    [Fact]
    public async Task A_server_that_stalls_mid_body_fails_rather_than_hanging()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload, new()
        {
            StallAfterBytes = 1024,
            StallFor = TimeSpan.FromSeconds(30),
        });
        var (source, paths) = Build(server, dir, payload,
            retry: RetryOptions.Default with { ReadTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAnyAsync<Exception>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    /// <summary>
    /// A cached file whose bytes have changed is not a cache hit. Without the digest check on the
    /// cached path, a corrupted or substituted DLL goes straight to the backend.
    /// </summary>
    [Fact]
    public async Task A_cached_file_whose_digest_no_longer_matches_is_refetched()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.UnlockerAssetFile("ea_app_version.dll"), [0xDE, 0xAD]);

        var bytes = await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.Equal(payload, bytes.ToArray());
        Assert.True(server.RequestCount > 0, "the corrupt cache entry was trusted");
    }

    /// <summary>
    /// Two callers racing the same client must both succeed. A fixed temp name lets the loser's
    /// FileMode.Create/FileShare.None open collide with the winner's still-open file, and the
    /// loser's catch then unlinks whatever sits at that name, possibly the winner's temp, so both
    /// fail. A per-call temp name has nothing to collide on.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_fetches_for_the_same_client_both_succeed()
    {
        using var dir = new TempDir();
        var payload = Payload();

        // Without the stall both fetches can serialise on a 4 KB local payload and the test passes
        // against a fixed temp name too. With it, fetch A still holds its temp open (FileShare.None)
        // while fetch B opens one, so a shared name collides every run. ReadTimeout must exceed
        // StallFor or both time out.
        await using var server = await TestFileServer.StartAsync(payload, new()
        {
            StallAfterBytes = 1024,
            StallFor = TimeSpan.FromMilliseconds(300),
        });
        var retry = RetryOptions.Default with { ReadTimeout = TimeSpan.FromSeconds(5) };
        var (sourceA, paths) = Build(server, dir, payload, retry: retry);
        var (sourceB, _) = Build(server, dir, payload, retry: retry);

        var results = await Task.WhenAll(
            sourceA.GetDllAsync(ClientKind.EaApp, CancellationToken.None),
            sourceB.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        foreach (var path in results)
        {
            Assert.Equal(payload, path.ToArray());
        }
        Assert.Empty(Directory.GetFiles(paths.Root, "*.incoming"));
    }

    /// <summary>
    /// A cache entry that cannot be read is re-fetched, not reported. The digest is the pinned one,
    /// so a reuse path that swallowed only mismatches would still have to read the file to learn
    /// that much: the request count is what says the fetch happened rather than the entry being
    /// trusted unread.
    /// </summary>
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_cache_entry_is_refetched_rather_than_failing()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload);

        var cached = paths.UnlockerAssetFile("ea_app_version.dll");
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(cached, payload);
        File.SetUnixFileMode(cached, UnixFileMode.None);

        var bytes = await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.Equal(payload, bytes.ToArray());
        Assert.True(server.RequestCount > 0, "the unreadable cache entry was trusted unread");

        // And the entry it could not read was replaced, so the next run reuses rather than
        // re-fetching for the rest of the install's life.
        Assert.Equal(payload, await File.ReadAllBytesAsync(cached));
    }

    /// <summary>
    /// A cache that cannot be written still returns the verified bytes. The cache makes the next
    /// call cheap; the bytes this call owes its caller were hashed against the pin before the
    /// rename was ever attempted, so a refused rename is not a reason to fail an install.
    ///
    /// On Windows this arrives as a concurrent caller — MoveFileEx will not replace a file another
    /// handle holds open, and a reader taking the cache-reuse path is enough to hold it. Arranged
    /// here without a second caller, because the same rename always succeeds on Unix: the directory
    /// is made unwritable while the transfer is still in flight, which is the same refusal from the
    /// rename's point of view, and leaves the cache genuinely unwritten rather than already correct.
    /// </summary>
    [PosixDenialFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_cache_that_cannot_be_written_still_returns_the_verified_bytes()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var payload = Payload();

        // StallFor zero: AtStall carries the ordering, so nothing waits on a clock.
        await using var server = await TestFileServer.StartAsync(payload, new()
        {
            StallAfterBytes = 1024,
            StallFor = TimeSpan.Zero,
        });

        var (source, paths) = Build(server, dir, payload);
        var cached = paths.UnlockerAssetFile("ea_app_version.dll");
        var locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = locked | UnixFileMode.UserWrite;
        var arranged = false;

        server.Options.AtStall = () =>
        {
            // The client opens its temp only once the response headers arrive, and those leave with
            // these first bytes, so the open is waited for rather than assumed: a directory locked
            // before the create would deny the create instead of the rename. Same shape as
            // LockChildHook's wait, and reported rather than assumed if it does not happen.
            for (var i = 0; i < 200 && Directory.GetFiles(paths.Root, "*.incoming").Length == 0; i++)
            {
                Thread.Sleep(50);
            }

            if (Directory.GetFiles(paths.Root, "*.incoming").Length == 0) return;

            File.SetUnixFileMode(paths.Root, locked);
            arranged = true;
        };

        try
        {
            var bytes = await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

            Assert.True(arranged, "the transfer never opened a temp file, so no rename was refused");
            Assert.Equal(payload, bytes.ToArray());

            // The cache really was not written, so this is the refused-rename path and not a run
            // that quietly succeeded in caching after all.
            Assert.False(File.Exists(cached));
        }
        finally
        {
            // Restored so the enclosing TempDir can be deleted.
            File.SetUnixFileMode(paths.Root, unlocked);
        }
    }

    /// <summary>
    /// The pins the application ships with, asserted as constants. Fetching them would reach
    /// github.com and would fail the day upstream rotates the asset.
    /// </summary>
    [Fact]
    public void The_shipped_pins_cover_both_clients_with_the_measured_digests()
    {
        var pins = StaticUnlockerAssetSource.ShippedPins;

        Assert.Equal(2, pins.Count);
        Assert.Equal("70553a2b4d53eddf1eeb290e346a9b562c71f52d46874bfb708e9d962469a736",
                     pins[ClientKind.EaApp].Sha256);
        Assert.Equal(245248, pins[ClientKind.EaApp].Size);
        Assert.Equal("cf784476719a93e3fb8457a2d4c4580b691b6d04592b9a4467acf563f30d2b83",
                     pins[ClientKind.Origin].Sha256);
        Assert.Equal(192512, pins[ClientKind.Origin].Size);
        Assert.All(pins.Values, p => Assert.StartsWith("https://", p.Url));
    }
}
