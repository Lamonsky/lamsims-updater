using System.Collections.Generic;
using Xunit;
using System.Xml.Linq;

namespace LamSims.App.Tests;

/// <summary>
/// The application's axaml, read as XML from this assembly's embedded resources.
/// </summary>
internal static class XamlSource
{
    public static XDocument Load(string logicalName)
    {
        using var stream = typeof(XamlSource).Assembly.GetManifestResourceStream(logicalName);

        Assert.True(stream is not null, $"'{logicalName}' is not an EmbeddedResource of this project");

        return XDocument.Load(stream!);
    }

    public static XDocument Window => Load("MainWindow.axaml");

    public static XDocument Styles => Load("Controls.axaml");

    public static XDocument Palette => Load("Palette.axaml");

    public static IReadOnlyList<XDocument> All =>
        [Window, Load("App.axaml"), Palette, Styles];
}
