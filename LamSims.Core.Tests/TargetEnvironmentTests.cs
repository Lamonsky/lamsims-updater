using Xunit;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class TargetEnvironmentTests
{
    // One case per source, because a single case passes with the source hardcoded.
    [Theory]
    [InlineData(EnvironmentSource.Wine, "~/.wine", "Wine, ~/.wine")]
    [InlineData(EnvironmentSource.Steam, "app 1222670", "Steam, app 1222670")]
    [InlineData(EnvironmentSource.Lutris, "ea-app", "Lutris, ea-app")]
    [InlineData(EnvironmentSource.Heroic, "default prefix", "Heroic, default prefix")]
    [InlineData(EnvironmentSource.Bottles, "XB36Hazard", "Bottles, XB36Hazard")]
    public void Describe_names_the_source_then_the_detail(
        EnvironmentSource source, string detail, string expected) =>
        Assert.Equal(expected, new TargetEnvironment(source, detail).Describe());

    // Native and Flatpak installs of one launcher produce prefixes whose source and detail are
    // identical, so without the flag their rows are indistinguishable.
    [Fact]
    public void Describe_marks_a_flatpak_home()
    {
        var native = new TargetEnvironment(EnvironmentSource.Steam, "app 1222670");
        var flatpak = new TargetEnvironment(EnvironmentSource.Steam, "app 1222670", Flatpak: true);

        Assert.Equal("Steam (Flatpak), app 1222670", flatpak.Describe());
        Assert.NotEqual(native.Describe(), flatpak.Describe());
    }

    [Fact]
    public void A_target_has_no_environment_by_default() =>
        Assert.Null(new UnlockerTarget("windows-native", ClientKind.EaApp, "/c", "EA app")
            .Environment);
}
