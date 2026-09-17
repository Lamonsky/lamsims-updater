using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Installing;
using LamSims.Core.Logging;

namespace LamSims.Core.Tests;

public class InstallLoggingTests
{
    [Fact]
    public async Task An_install_logs_where_it_went_and_how_many_entries_it_wrote()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();
        var installer = new ZipInstaller(new InstallStateStore(dir.Path), log: log);
        var archive = ZipFixture.WriteArchive(dir, ("EP01/data.package", 1024), ("EP01/x.bin", 16));
        var pack = ZipFixture.Pack();
        var game = Path.Combine(dir.Path, "game");
        Directory.CreateDirectory(game);

        var result = await installer.InstallAsync(
            pack, archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.True(log.Logged($"Installing into {game}"));
        Assert.True(log.Logged("Installed EP01, 2 entries"));
    }

    /// <summary>
    /// The failing branch, which the success test cannot reach. Without it a user sees the row go
    /// red and the log say nothing about why.
    /// </summary>
    [Fact]
    public async Task A_failed_install_logs_the_reason_as_an_error()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();
        var installer = new ZipInstaller(
            new InstallStateStore(dir.Path), availableBytes: _ => 1, log: log);
        var archive = ZipFixture.WriteArchive(dir, ("EP01/data.package", 100_000));
        var pack = ZipFixture.Pack();
        var game = Path.Combine(dir.Path, "game");
        Directory.CreateDirectory(game);

        var result = await installer.InstallAsync(
            pack, archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.InsufficientSpace, result.Outcome);
        Assert.True(log.Logged("Install failed", LogSeverity.Error));
    }

    /// <summary>
    /// The other failure catch: <see cref="InsufficientDiskSpaceException"/> above never reaches
    /// this one, and a corrupt archive is what a scanner or an interrupted download actually
    /// leaves behind, so this is the site a real "Install failed" row is most likely to come from.
    /// </summary>
    [Fact]
    public async Task A_corrupt_archive_also_logs_the_reason_as_an_error()
    {
        using var dir = new TempDir();
        var log = new RecordingLogSink();
        var installer = new ZipInstaller(new InstallStateStore(dir.Path), log: log);
        var archive = dir.File("EP01.zip");
        await File.WriteAllTextAsync(archive, "this is not a zip file");
        var pack = ZipFixture.Pack();
        var game = Path.Combine(dir.Path, "game");
        Directory.CreateDirectory(game);

        var result = await installer.InstallAsync(
            pack, archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.True(log.Logged("Install failed", LogSeverity.Error));
    }
}
