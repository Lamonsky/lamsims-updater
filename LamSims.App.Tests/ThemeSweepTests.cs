using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LamSims.App.Tests;

/// <summary>
/// Static checks over the theme files, in the same spirit as MainWindowBindingTests and under the
/// same limit: XML and reflection only, never an Avalonia type, so this project keeps needing no
/// display. Rendering proves a style reached a control but only for controls a test constructs;
/// this visits every reference in the files and can only check names. Neither subsumes the other.
///
/// Two limits. Resource SCOPE is not modelled: DefinedKeys() unions the keys of all four
/// documents, so a {DynamicResource Foo} in Controls.axaml would be reported as resolved by a Foo
/// defined only in MainWindow.axaml, which at runtime would not resolve, a window-local resource
/// being invisible to an application-level style. The scan also reads ATTRIBUTE-form references
/// only: a {StaticResource} written as element content, or a resource reached through a Binding,
/// is not seen. Neither is live in this application today.
/// </summary>
public class ThemeSweepTests
{
    private static readonly Regex ResourceReference =
        new(@"\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled);

    private static HashSet<string> DefinedKeys() =>
        XamlSource.All
            .SelectMany(d => d.Descendants())
            .Select(e => e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<(string File, string Key, string Where)> References() =>
        new[] { ("MainWindow.axaml", XamlSource.Window), ("App.axaml", XamlSource.Load("App.axaml")),
                ("Palette.axaml", XamlSource.Palette), ("Controls.axaml", XamlSource.Styles) }
            .SelectMany(pair => pair.Item2.Descendants()
                .SelectMany(e => e.Attributes()
                    .SelectMany(a => ResourceReference.Matches(a.Value)
                        .Select(m => (pair.Item1, m.Groups["key"].Value,
                                      $"{e.Name.LocalName}.{a.Name.LocalName}")))));

    [Fact]
    public void Every_resource_reference_resolves_to_a_defined_key()
    {
        var defined = DefinedKeys();

        var dangling = References()
            .Where(r => !defined.Contains(r.Key))
            .Select(r => $"{r.File}: {r.Where} references '{r.Key}', which nothing defines")
            .ToList();

        Assert.True(dangling.Count == 0, string.Join(Environment.NewLine, dangling));
    }

    /// <summary>
    /// Named for what it does: it reads the STYLES and proves the sweep above had references to
    /// examine. It says nothing about what the palette defines - the old name claimed that, and a
    /// reader who believed it could weaken the dangling-reference check on the strength of a
    /// guarantee this never made.
    /// </summary>
    [Fact]
    public void The_style_sweep_is_not_reading_an_empty_file()
    {
        // Guards the check above against passing vacuously. Its unique catch is the regex silently
        // ceasing to match while the file stays embedded; a styles file that stopped being
        // embedded is already intercepted by XamlSource.Load's own null-stream assertion. Either
        // way "no dangling references" would be true of an empty set, which is the shape of a test
        // that cannot fail.
        var fromStyles = XamlSource.Styles.Descendants()
            .SelectMany(e => e.Attributes())
            .SelectMany(a => ResourceReference.Matches(a.Value).Select(m => m.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(fromStyles);
        Assert.Contains("RedBrush", fromStyles);
        Assert.Contains("YellowBrush", fromStyles);
    }

    /// <summary>
    /// Generic family names — the CSS keywords, which are also fontconfig's aliases. Avalonia
    /// resolves a FontFamily through the platform font manager, and no platform font manager
    /// knows them: they match nothing and fall through to the default UI face.
    /// </summary>
    private static readonly string[] GenericFamilies =
        ["monospace", "sans-serif", "serif", "cursive", "fantasy", "system-ui", "ui-monospace"];

    /// <summary>
    /// The log shipped with FontFamily="monospace". It resolved to proportional Noto Sans — so the
    /// log was never monospaced — and it cost the renderer 363ms a frame against 24ms once the
    /// family named something real, which is what made the window resize at about 7 FPS. A static
    /// check, because the runtime one cannot be written portably: proving a family resolved needs
    /// the real font manager, and the headless one this suite runs on stubs every glyph to the
    /// same width, under which any family at all measures as monospaced.
    /// </summary>
    [Fact]
    public void No_style_asks_for_a_font_family_no_font_manager_can_resolve()
    {
        var setters = XamlSource.Styles.Descendants()
            .Where(e => e.Name.LocalName == "Setter"
                        && e.Attribute("Property")?.Value == "FontFamily")
            .Select(e => e.Attribute("Value")?.Value ?? "")
            .ToList();

        Assert.NotEmpty(setters);

        var generic = setters
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(name => GenericFamilies.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(name => $"Controls.axaml asks for the generic family '{name}'. Avalonia cannot "
                            + "resolve one: name real families instead, most wanted first.")
            .ToList();

        Assert.True(generic.Count == 0, string.Join(Environment.NewLine, generic));
    }

    /// <summary>
    /// The bundled Inter face is registered by WithInterFont() under the key "fonts:Inter", and
    /// only a family carrying that scheme reaches it. A bare "Inter" resolves to the platform
    /// default instead — no error, no missing glyphs, just a different face than the one shipped —
    /// which is how the application ran for its whole life before this. The window is where the
    /// family belongs: every control inherits it, popup content included.
    /// </summary>
    [Fact]
    public void The_window_style_names_the_bundled_face_through_its_collection()
    {
        var value = XamlSource.Styles.Descendants()
            .Where(e => e.Name.LocalName == "Style" && e.Attribute("Selector")?.Value == "Window")
            .SelectMany(e => e.Descendants().Where(d => d.Name.LocalName == "Setter"))
            .Where(setter => setter.Attribute("Property")?.Value == "FontFamily")
            .Select(setter => setter.Attribute("Value")?.Value ?? "")
            .SingleOrDefault();

        Assert.False(value is null, "the Window style sets no FontFamily, so the window renders in "
                                   + "the platform's default face and the bundled Inter is unused");

        Assert.True(value!.StartsWith("fonts:", StringComparison.Ordinal)
                    || value.StartsWith("avares://", StringComparison.Ordinal),
            $"the Window style asks for '{value}'. Without a fonts: or avares:// scheme this names "
            + "no registered collection and falls back to the platform default.");
    }

    /// <summary>Class names the window applies, whether literally or through a binding.</summary>
    private static HashSet<string> AppliedClasses()
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in XamlSource.Window.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;

                // Classes.warning="{Binding IsWarning}"
                if (name.StartsWith("Classes.", StringComparison.Ordinal))
                {
                    applied.Add(name["Classes.".Length..]);
                }
                // Classes="banner secondary"
                else if (name == "Classes")
                {
                    foreach (var c in attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        applied.Add(c);
                    }
                }
            }
        }

        return applied;
    }

    /// <summary>
    /// Class names the styles file selects on. This re-derives the selector list rather than
    /// sharing the one in LamSims.App.ViewTests, which reflects over
    /// Avalonia types and this project may not. Extracting a '.class' from a string needs no such
    /// dependency, so the duplication stops here.
    /// </summary>
    private static HashSet<string> StyledClasses() =>
        XamlSource.Styles.Descendants()
            .Where(e => e.Name.LocalName == "Style")
            .Select(e => e.Attribute("Selector")?.Value ?? "")
            .SelectMany(selector => Regex.Matches(selector, @"\.(?<class>[A-Za-z_][A-Za-z0-9_-]*)")
                .Select(m => m.Groups["class"].Value))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_class_the_window_applies_has_a_style_behind_it()
    {
        var applied = AppliedClasses();
        var styled = StyledClasses();

        Assert.NotEmpty(applied);

        var colourless = applied.Except(styled)
            .Select(c => $"MainWindow.axaml applies class '{c}', which no selector in "
                         + "Controls.axaml matches; it is bound and does nothing")
            .ToList();

        Assert.True(colourless.Count == 0, string.Join(Environment.NewLine, colourless));
    }

    [Fact]
    public void Every_class_the_styles_select_is_applied_somewhere()
    {
        var applied = AppliedClasses();
        var styled = StyledClasses();

        Assert.NotEmpty(styled);

        var dead = styled.Except(applied)
            .Select(c => $"Controls.axaml styles class '{c}', which MainWindow.axaml never "
                         + "applies; the style is dead")
            .ToList();

        Assert.True(dead.Count == 0, string.Join(Environment.NewLine, dead));
    }
}
