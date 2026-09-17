using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LamSims.App.ViewTests;

/// <summary>
/// Static checks over Controls.axaml. Rendering proves a style reached a control, but only for the
/// controls a test constructs; this visits every selector in the file and can only check names.
///
/// Plain [Fact], not [AvaloniaFact]: this starts no application and shows no window. It lives in
/// this project only because it reflects over Avalonia types, which LamSims.App.Tests may not.
///
/// There is no setter-property check here: Avalonia's XAML compiler already rejects a wrong Setter
/// Property name at build time (AVLN2000). It does NOT validate pseudo-classes, so ":hover"
/// compiles clean, which is why the selector check below is worth writing.
///
/// The TYPE half is not exercised by current markup, since every type Controls.axaml names is a
/// stock Avalonia control that resolves trivially; it guards against a future selector.
/// </summary>
public class SelectorSweepTests
{
    private static XDocument Styles()
    {
        using var stream = typeof(SelectorSweepTests).Assembly.GetManifestResourceStream("Controls.axaml");

        Assert.True(stream is not null, "'Controls.axaml' is not an EmbeddedResource of this project");

        return XDocument.Load(stream!);
    }

    /// <summary>
    /// The pseudo-classes this application is allowed to use. An unrecognised one fails rather than
    /// being skipped: Avalonia silently ignores a selector whose pseudo-class never matches, so
    /// ":hover" (the CSS name, which Avalonia does not have) would be dead markup with no build
    /// error and no rendering test able to see it.
    /// </summary>
    private static readonly HashSet<string> KnownPseudoClasses = new(StringComparer.Ordinal)
    {
        "pointerover", "pressed", "disabled", "focus", "focus-within", "focus-visible",
        "checked", "unchecked", "indeterminate", "selected", "expanded", "empty", "open",
        "horizontal", "vertical", "dragging", "flyout-open",
    };

    private static List<(string Selector, XElement Style)> Selectors() =>
        Styles().Descendants()
            .Where(e => e.Name.LocalName == "Style")
            .Select(e => (Selector: e.Attribute("Selector")?.Value ?? "", Style: e))
            .Where(pair => pair.Selector.Length > 0)
            .ToList();

    private static readonly Type[] SearchAnchors =
    [
        typeof(Avalonia.Controls.Button),
        typeof(Avalonia.Visual),
    ];

    private static Type? ResolveType(string name) =>
        SearchAnchors
            .SelectMany(anchor => anchor.Assembly.GetExportedTypes())
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The last type named in a selector governs its setters: for
    /// "Button:pointerover /template/ ContentPresenter" that is ContentPresenter, not Button.
    /// '#' terminates the type the same way '.' and ':' do: "ContentPresenter#PART_ContentPresenter"
    /// names the type ContentPresenter and the element name PART_ContentPresenter. A selector that
    /// gave a name with no type at all still yields "" here and still fails to resolve, which is
    /// the behaviour to keep: this recognises the name syntax, it does not excuse a missing type.
    /// </summary>
    private static string TargetTypeName(string selector)
    {
        var segment = selector.Split("/template/", StringSplitOptions.TrimEntries).Last();
        var last = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();

        // ":is(TextBlock).log-line" names TextBlock, and splitting on ':' would name "". Reading
        // the type out of the wrapper rather than skipping the form keeps the type half of this
        // check alive for exactly the selectors that need it: a subclass match is the only way a
        // style reaches the log's SelectableTextBlock columns, so those four are all written this
        // way, and a typo inside :is() has to fail here or nothing catches it.
        if (last.StartsWith(":is(", StringComparison.Ordinal) && IsFunction.Match(last) is { Success: true } m)
        {
            return m.Groups["type"].Value;
        }

        return last.Split('.', ':', '#')[0];
    }

    /// <summary>
    /// Avalonia's functional subclass selector. Not a pseudo-class: it takes a type argument and
    /// widens the type match, so the pseudo-class scan below has to strip it before looking for
    /// ':name' or every one of these selectors reports ':is' as an unknown pseudo-class.
    /// </summary>
    private static readonly Regex IsFunction =
        new(@":is\((?<type>[A-Za-z_][A-Za-z0-9_]*)\)", RegexOptions.Compiled);

    /// <summary>
    /// Every complaint one selector earns. The character class is deliberately case-INSENSITIVE
    /// while <see cref="KnownPseudoClasses"/> compares with Ordinal: Avalonia 12.0.4 parses
    /// ":Pointerover" and ":POINTEROVER" without complaint and then silently never matches them,
    /// so a lowercase-only pattern would skip the mis-cased typo entirely rather than reject it.
    /// </summary>
    private static IEnumerable<string> ProblemsWith(string selector)
    {
        var typeName = TargetTypeName(selector);

        if (ResolveType(typeName) is null)
        {
            yield return $"selector '{selector}': '{typeName}' is not a type this application can see";
        }

        foreach (Match match in Regex.Matches(IsFunction.Replace(selector, ""),
                                              @":(?<pseudo>[A-Za-z][A-Za-z-]*)"))
        {
            var pseudo = match.Groups["pseudo"].Value;

            if (!KnownPseudoClasses.Contains(pseudo))
            {
                yield return $"selector '{selector}': ':{pseudo}' is not a pseudo-class this project "
                             + "knows. If Avalonia really has it, add it to KnownPseudoClasses "
                             + "deliberately.";
            }
        }
    }

    [Fact]
    public void Every_selector_names_a_real_type_and_a_known_pseudo_class()
    {
        var selectors = Selectors();

        // Not decoration: without this, a Controls.axaml that failed to embed would make this
        // check and the next one pass over an empty sequence.
        Assert.NotEmpty(selectors);

        var problems = selectors.SelectMany(pair => ProblemsWith(pair.Selector)).ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData("ListBoxItem:Pointerover")]
    [InlineData("ListBoxItem:POINTEROVER")]
    [InlineData("Button:hover")]
    [InlineData("Button:pointerover /template/ ContentPresenter:Pressed")]
    public void A_pseudo_class_avalonia_will_silently_ignore_is_reported(string selector)
    {
        // Measured against Avalonia 12.0.4 through AvaloniaRuntimeXamlLoader: each of these parses
        // with no exception and then matches nothing, so the compiler cannot catch them and a
        // rendering test would only see a style that quietly did not apply.
        Assert.NotEmpty(ProblemsWith(selector));
    }

    /// <summary>
    /// The subclass form, both ways round: a real type inside :is() passes and ':is' itself is not
    /// mistaken for a pseudo-class, while a type that does not exist inside :is() is still caught.
    /// Without the second case the parser change would excuse the form instead of reading it.
    /// </summary>
    [Theory]
    [InlineData(":is(TextBlock).log-line")]
    [InlineData(":is(TextBlock).secondary")]
    public void The_subclass_selector_form_is_understood(string selector) =>
        Assert.Empty(ProblemsWith(selector));

    [Fact]
    public void A_type_that_does_not_exist_inside_the_subclass_form_is_still_reported() =>
        Assert.NotEmpty(ProblemsWith(":is(TextBlok).log-line"));

    [Theory]
    [InlineData("ListBoxItem:pointerover")]
    [InlineData("ListBoxItem:selected /template/ ContentPresenter#PART_ContentPresenter")]
    [InlineData("Button:pointerover /template/ ContentPresenter")]
    public void A_correctly_spelled_selector_is_not_reported(string selector)
    {
        // The contrast that stops the check above from passing by rejecting everything.
        Assert.Empty(ProblemsWith(selector));
    }
}
