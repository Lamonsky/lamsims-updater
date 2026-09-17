using System;
using Xunit;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

/// <summary>
/// The shipped version is load-bearing: release.yml refuses to publish when a release tag
/// and this value disagree. A default 1.0.0 would make every
/// build offer its own users a perpetual update.
/// </summary>
public class VersionTests
{
    [Fact]
    public void Core_carries_the_shipped_version()
    {
        var version = typeof(AppPaths).Assembly.GetName().Version;

        Assert.NotNull(version);
        Assert.Equal(new Version(2, 0, 0, 0), version);
    }
}
