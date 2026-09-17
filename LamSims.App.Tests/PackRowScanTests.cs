using LamSims.Core.Scanning;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class PackRowScanTests
{
    private static PackRowViewModel Row() => new(Packs.Entry(), new RecordingQueue());

    private static PackScanResult Scan(PackInstallState state, params string[] missing) =>
        new("EP01", state, missing, null, null);

    [Fact]
    public void A_pack_that_is_not_installed_shows_its_size()
    {
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));

        Assert.Equal("Not installed", row.StatusText);
        Assert.Equal("12.4 GB", row.SizeText);
        Assert.True(row.IsCheckable);
    }

    [Fact]
    public void An_unverified_install_says_so_and_stays_checkable()
    {
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.InstalledUnverified));

        Assert.Equal("Installed (not verified by this tool)", row.StatusText);
        Assert.True(row.IsCheckable);
    }

    [Fact]
    public void A_partial_install_names_how_many_directories_are_missing()
    {
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.Partial, "EP01"));

        Assert.Equal("Partial — 1 folder missing", row.StatusText);
        Assert.True(row.IsCheckable);
    }

    [Fact]
    public void A_partial_install_with_no_missing_directory_says_the_install_did_not_finish()
    {
        // Core's contract: Partial with an empty MissingDirs means every install directory is
        // present and the journal says the extraction never completed. The two Partial cases
        // must read differently, and this is the only place that distinguishes them.
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.Partial));

        Assert.Equal("Partial — an install started and did not finish", row.StatusText);
    }

    [Fact]
    public void An_installed_pack_is_not_checkable_and_its_check_is_cleared()
    {
        var row = Row();
        row.IsChecked = true;
        row.ApplyScan(Scan(PackInstallState.Installed));

        Assert.Equal("Installed", row.StatusText);
        Assert.False(row.IsCheckable);
        Assert.False(row.IsChecked);
    }

    [Fact]
    public void The_context_menu_can_force_an_installed_pack_checkable()
    {
        // Through the command, because that is what the menu item binds. Setting the property
        // directly would leave the command itself uncovered, and it is the only route the
        // application actually offers.
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.Installed));
        row.ReinstallCommand.Execute(null);

        Assert.True(row.ForceCheckable);
        Assert.True(row.IsCheckable);

        // Cleared, not checked: reinstall clears the disable and nothing else.
        Assert.False(row.IsChecked);
    }

    [Fact]
    public void A_row_that_is_not_in_the_queue_offers_neither_control()
    {
        var row = Row();
        row.ApplyScan(Scan(PackInstallState.NotInstalled, "EP01"));

        Assert.False(row.CanCancel);
        Assert.False(row.CanRemove);
        Assert.False(row.CancelCommand.CanExecute(null));
        Assert.False(row.RemoveCommand.CanExecute(null));
    }
}
