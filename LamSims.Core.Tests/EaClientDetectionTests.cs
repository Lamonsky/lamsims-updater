using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class EaClientDetectionTests
{
    private static (EaClientUnlockerBackend Backend, FakeUnlockerHost Host) Build(TempDir dir)
    {
        var host = new FakeUnlockerHost();
        var backend = new EaClientUnlockerBackend(
            host,
            new UnlockerPaths(Path.Combine(dir.Path, "roaming"), Path.Combine(dir.Path, "common")),
            new AppPaths(Path.Combine(dir.Path, "app")),
            new FakeDelayProvider());
        return (backend, host);
    }

    /// <summary>Creates the client's directory and returns the exe path the registry would hold.</summary>
    private static string InstalledClient(TempDir dir, string folder, string exe)
    {
        var directory = Path.Combine(dir.Path, folder);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, exe);
    }

    [Theory]
    [InlineData(ClientRegistryKey.EaDesktop, ClientKind.EaApp, "EA app")]
    [InlineData(ClientRegistryKey.EaDesktopWow6432, ClientKind.EaApp, "EA app")]
    [InlineData(ClientRegistryKey.OriginWow6432, ClientKind.Origin, "Origin")]
    [InlineData(ClientRegistryKey.Origin, ClientKind.Origin, "Origin")]
    public async Task Each_key_alone_yields_its_client(ClientRegistryKey key, ClientKind kind,
                                                       string display)
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.ClientPaths[key] = InstalledClient(dir, "client", "EADesktop.exe");

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        var target = Assert.Single(targets);
        Assert.Equal(kind, target.Client);
        Assert.Equal(display, target.DisplayName);
        Assert.Equal("windows-native", target.BackendId);
        Assert.Equal(Path.Combine(dir.Path, "client"), target.ClientPath);
    }

    // Both the count and the order. Returning one target satisfies "contains EaApp", and
    // returning them in registry-dictionary order satisfies "contains both", so neither of
    // those alone would catch a regression to first-hit-wins.
    [Fact]
    public async Task Both_clients_are_returned_when_both_are_present()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.ClientPaths[ClientRegistryKey.EaDesktop] = InstalledClient(dir, "ea", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.Origin] = InstalledClient(dir, "origin", "Origin.exe");

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Equal(2, targets.Count);
        Assert.Equal(ClientKind.EaApp, targets[0].Client);
        Assert.Equal(ClientKind.Origin, targets[1].Client);
        Assert.Equal(Path.Combine(dir.Path, "ea"), targets[0].ClientPath);
        Assert.Equal(Path.Combine(dir.Path, "origin"), targets[1].ClientPath);
    }

    // The Origin 64-bit and 32-bit views name one directory on a real machine, so accumulation
    // without deduplication shows every Origin install twice. Both keys hold the same string
    // here, so which entry won is not observable; ordering is asserted by
    // Both_clients_are_returned_when_both_are_present instead.
    [Fact]
    public async Task Two_registry_views_of_one_directory_yield_one_target()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        var shared = InstalledClient(dir, "origin", "Origin.exe");
        host.ClientPaths[ClientRegistryKey.OriginWow6432] = shared;
        host.ClientPaths[ClientRegistryKey.Origin] = shared;

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Equal(ClientKind.Origin, Assert.Single(targets).Client);
        Assert.Equal(Path.Combine(dir.Path, "origin"), targets[0].ClientPath);
    }

    // The same for the EA app's two views, which a real 64-bit prefix carries: the EA Desktop
    // key and its Wow6432Node counterpart both name EADesktop.exe.
    [Fact]
    public async Task Two_ea_app_views_of_one_directory_yield_one_target()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        var shared = InstalledClient(dir, "ea", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.EaDesktop] = shared;
        host.ClientPaths[ClientRegistryKey.EaDesktopWow6432] = shared;

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Equal(ClientKind.EaApp, Assert.Single(targets).Client);
    }

    // Deduplication runs on PathIdentity semantics, not string equality. A registry that spells
    // one directory two ways is the normal case, and an ordinal HashSet would let it through.
    [Fact]
    public async Task Deduplication_ignores_case_differences_in_the_registry_value()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        var directory = Path.Combine(dir.Path, "origin");
        Directory.CreateDirectory(directory);
        var shouted = Path.Combine(dir.Path, "ORIGIN");
        Directory.CreateDirectory(shouted);
        host.ClientPaths[ClientRegistryKey.OriginWow6432] = Path.Combine(directory, "Origin.exe");
        host.ClientPaths[ClientRegistryKey.Origin] = Path.Combine(shouted, "Origin.exe");

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Single(targets);
    }

    [Fact]
    public async Task A_key_whose_directory_does_not_exist_is_skipped_not_returned()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.ClientPaths[ClientRegistryKey.EaDesktop] =
            Path.Combine(dir.Path, "not-installed", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.Origin] = InstalledClient(dir, "origin", "Origin.exe");

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Equal(ClientKind.Origin, Assert.Single(targets).Client);
    }

    [Fact]
    public async Task No_keys_at_all_yields_an_empty_list_rather_than_throwing()
    {
        using var dir = new TempDir();
        var (backend, _) = Build(dir);

        Assert.Empty(await backend.DetectTargetsAsync(CancellationToken.None));
    }

    // Every key is scripted present, because an empty list is also what "no keys at all" produces;
    // on Linux a backend that ignored IsAvailable would call registry members that throw.
    [Fact]
    public async Task An_unavailable_host_is_not_consulted_at_all()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.IsAvailable = false;
        foreach (var key in Enum.GetValues<ClientRegistryKey>())
            host.ClientPaths[key] = InstalledClient(dir, "client", "EADesktop.exe");

        Assert.Empty(await backend.DetectTargetsAsync(CancellationToken.None));
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("ReadClientPath", StringComparison.Ordinal));
        Assert.False(backend.IsSupported);
    }

    [Fact]
    public async Task Status_is_installed_exactly_when_version_dll_is_present()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        var exe = InstalledClient(dir, "client", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.EaDesktop] = exe;
        var target = (await backend.DetectTargetsAsync(CancellationToken.None))[0];

        Assert.Equal(UnlockerState.NotInstalled,
                     (await backend.GetStatusAsync(target, CancellationToken.None)).State);

        await File.WriteAllTextAsync(Path.Combine(target.ClientPath, "version.dll"), "x");

        Assert.Equal(UnlockerState.Installed,
                     (await backend.GetStatusAsync(target, CancellationToken.None)).State);
    }

    // The detail is asserted alongside the state so an operator sees which directory vanished.
    [Fact]
    public async Task Status_is_unknown_when_the_client_directory_has_vanished()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        var exe = InstalledClient(dir, "client", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.EaDesktop] = exe;
        var target = (await backend.DetectTargetsAsync(CancellationToken.None))[0];

        Directory.Delete(target.ClientPath, recursive: true);

        var status = await backend.GetStatusAsync(target, CancellationToken.None);

        Assert.Equal(UnlockerState.Unknown, status.State);
        Assert.Contains(target.ClientPath, status.Detail);
    }
}
