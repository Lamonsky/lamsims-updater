using System;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

/// <summary>
/// The App assembly is the one that ships, so its version is what a release tag is checked
/// against and what a self-update comparison reads. Core carries the same number through the same
/// import, but only this assertion covers the binary a user runs.
/// </summary>
public class VersionTests
{
    [Fact]
    public void The_app_assembly_carries_the_shipped_version()
    {
        var version = typeof(MainViewModel).Assembly.GetName().Version;

        Assert.NotNull(version);
        Assert.Equal(new Version(2, 0, 0, 0), version);
    }
}
