using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(LamSims.App.ViewTests.TestAppBuilder))]

namespace LamSims.App.ViewTests;

/// <summary>
/// Configures the shipped <see cref="LamSims.App.App"/>, not a test double, so that a theme which
/// fails to load, a ResourceInclude with a wrong avares:// path, or a malformed selector fails this
/// suite instead of passing under a substitute that never had the problem.
///
/// Program.BuildAvaloniaApp() cannot be reused: it calls UsePlatformDetect(), and Program is
/// internal to an assembly whose InternalsVisibleTo names only LamSims.App.Tests.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<LamSims.App.App>()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
