using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class UnlockerServiceTests
{
    private sealed class StubBackend(string id, bool supported, params UnlockerTarget[] targets)
        : IUnlockerBackend
    {
        public string Id => id;
        public bool IsSupported => supported;
        public int DetectCalls { get; private set; }
        public List<string> Installed { get; } = [];

        public Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct)
        {
            DetectCalls++;
            return Task.FromResult<IReadOnlyList<UnlockerTarget>>(targets);
        }

        public Task<UnlockerStatus> GetStatusAsync(UnlockerTarget t, CancellationToken ct) =>
            Task.FromResult(new UnlockerStatus(UnlockerState.NotInstalled, null));

        public Task<UnlockerResult> InstallAsync(UnlockerTarget t, IUnlockerAssetSource a,
                                                IProgress<UnlockerProgress> p, CancellationToken ct)
        {
            Installed.Add(t.ClientPath);
            return Task.FromResult(UnlockerResult.Ok());
        }

        public Task<UnlockerResult> RemoveAsync(UnlockerTarget t, IProgress<UnlockerProgress> p,
                                               CancellationToken ct) =>
            Task.FromResult(UnlockerResult.Ok());
    }

    private static UnlockerTarget Target(string backendId, string path) =>
        new(backendId, ClientKind.EaApp, path, "EA app");

    [Fact]
    public async Task Targets_from_every_supported_backend_arrive_in_one_flat_list()
    {
        var a = new StubBackend("a", true, Target("a", "/one"));
        var b = new StubBackend("b", true, Target("b", "/two"), Target("b", "/three"));
        var service = new UnlockerService([a, b]);

        var targets = await service.DetectAllAsync(CancellationToken.None);

        Assert.Equal(["/one", "/two", "/three"], targets.Select(t => t.ClientPath));
    }

    /// <summary>
    /// The pair: an unsupported backend contributes nothing AND is never asked. Asserting only the
    /// result would pass against a service that called into a backend whose members throw off-platform.
    /// </summary>
    [Fact]
    public async Task An_unsupported_backend_is_never_consulted()
    {
        var off = new StubBackend("off", false, Target("off", "/nope"));
        var service = new UnlockerService([off]);

        Assert.Empty(await service.DetectAllAsync(CancellationToken.None));
        Assert.Equal(0, off.DetectCalls);
        Assert.False(service.IsSupported);
    }

    [Fact]
    public void IsSupported_is_true_when_any_backend_is()
    {
        Assert.True(new UnlockerService(
            [new StubBackend("off", false), new StubBackend("on", true)]).IsSupported);
    }

    /// <summary>
    /// Routing by BackendId, not by position. Two backends both offering an EA app target is exactly
    /// the case a positional lookup gets wrong, and the case a Wine backend creates.
    /// </summary>
    [Fact]
    public async Task A_target_is_installed_by_the_backend_whose_id_it_carries()
    {
        var a = new StubBackend("a", true, Target("a", "/one"));
        var b = new StubBackend("b", true, Target("b", "/two"));
        var service = new UnlockerService([a, b]);

        await service.InstallAsync(Target("b", "/two"), new ThrowingAssets(),
                                  new SyncProgress<UnlockerProgress>(_ => { }), CancellationToken.None);

        Assert.Empty(a.Installed);
        Assert.Equal(["/two"], b.Installed);
    }

    [Fact]
    public async Task A_target_naming_an_unknown_backend_is_an_error_not_a_silent_no_op()
    {
        var service = new UnlockerService([new StubBackend("a", true)]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(Target("gone", "/x"), new ThrowingAssets(),
                                      new SyncProgress<UnlockerProgress>(_ => { }),
                                      CancellationToken.None));
    }

    private sealed class ThrowingAssets : IUnlockerAssetSource
    {
        public Task<ReadOnlyMemory<byte>> GetDllAsync(ClientKind client, CancellationToken ct) =>
            throw new InvalidOperationException("the stub backend must not fetch");
    }
}
