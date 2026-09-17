using System.Threading.Tasks;
using Xunit;

namespace LamSims.App.Tests;

public class BuildSmokeTests
{
    [Fact(Timeout = 15000)]
    public async Task The_application_builder_can_be_constructed_without_a_display()
    {
        // Configure<App>() builds the AppBuilder graph without starting a windowing backend,
        // which is the boundary this test project must never cross. Run on a background thread
        // so the test is genuinely async: xunit's Timeout only applies to async Task Facts.
        var builder = await Task.Run(() => Program.BuildAvaloniaApp());

        Assert.NotNull(builder);
    }
}
