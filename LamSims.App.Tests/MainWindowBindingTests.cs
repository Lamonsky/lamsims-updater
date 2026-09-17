using System;
using Avalonia;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

/// <summary>
/// Reads MainWindow.axaml as XML and resolves every <c>{Binding}</c> against the view model that
/// governs the DataContext at that point in the tree.
///
/// This exists because nothing else in the suite touches the view at all: compiled bindings are
/// deliberately off in this project (LamSims.App.csproj), so a binding naming a member that does
/// not exist on its DataContext resolves to null at runtime with no build error and no exception.
/// The whole row context menu was attached to the ListBox rather than to the item template, so
/// Cancel, Remove and Reinstall bound against MainViewModel, were dead in the shipped
/// application, and the suite could not tell.
///
/// Reflection and XML only, never Avalonia: LamSims.App.Tests must not touch a windowing type,
/// construct a Window or a TopLevel, or need a display. The cost of that is that this checks
/// NAMES, not that a binding produces the right value, which is the failure class that was live.
/// </summary>
public class MainWindowBindingTests
{
    /// <summary>
    /// The axaml is an EmbeddedResource of this test project (see the csproj), rather than being
    /// located by walking up from AppContext.BaseDirectory: the resource is refreshed by the same
    /// build that compiles the assertion, and it cannot go missing because the test ran from an
    /// unexpected working directory.
    /// </summary>
    private static XDocument LoadWindow() => XamlSource.Window;

    [Fact]
    public void Every_binding_in_the_window_names_a_member_of_its_own_data_context()
    {
        var unresolved = new List<string>();

        Walk(LoadWindow().Root!, typeof(MainViewModel), parent: null, unresolved);

        // Not Assert.Empty: the whole value of this test is that the failure NAMES the binding
        // and the member, since a silent null binding is the thing being caught.
        Assert.True(unresolved.Count == 0, string.Join(Environment.NewLine, unresolved));
    }

    [Fact]
    public void The_row_context_menu_binds_against_the_row_and_not_against_the_window()
    {
        // Named separately from the sweep above so the regression that motivated this file fails
        // by name rather than as one line inside a list. ForceCheckable has no other route
        // into the application: without a reachable Reinstall item an Installed row's checkbox
        // can never be re-enabled.
        var menu = LoadWindow().Descendants()
            .Single(e => e.Name.LocalName == "ContextMenu");

        var ancestors = Ancestry(menu).Select(e => e.Name.LocalName).ToList();

        Assert.True(ancestors.Contains("DataTemplate") && ancestors.Contains("ListBox"),
            "the row ContextMenu must sit inside the ListBox's item template so that it inherits "
            + "the row's DataContext. Its ancestors are: " + string.Join(" < ", ancestors));

        var listBox = Ancestry(menu).First(e => e.Name.LocalName == "ListBox");

        Assert.Equal("{Binding FilteredRows}", listBox.Attribute("ItemsSource")?.Value);

        foreach (var name in (string[])["CancelCommand", "RemoveCommand", "ReinstallCommand"])
        {
            Assert.Contains(menu.Descendants(),
                item => item.Attribute("Command")?.Value == "{Binding " + name + "}");

            Assert.NotNull(Member(typeof(PackRowViewModel), name));
        }
    }

    /// <summary>
    /// An element carrying DataContext="{Binding Prop}" re-roots its subtree at Prop's declared type.
    /// Without this the sweep resolves everything inside the unlocker region against MainViewModel and
    /// reports all of it unresolved, and the tempting "fix" would be to stop checking the region at
    /// all, which is how the row context menu shipped dead in the first place.
    /// </summary>
    [Fact]
    public void A_nested_data_context_re_roots_the_expected_type_for_its_subtree()
    {
        var xml = XElement.Parse("""
            <Border xmlns="https://github.com/avaloniaui" DataContext="{Binding Unlocker}">
              <TextBlock IsVisible="{Binding IsSupported}" />
            </Border>
            """);

        var unresolved = new List<string>();
        Walk(xml, typeof(MainViewModel), parent: null, unresolved);

        Assert.Empty(unresolved);
    }

    /// <summary>
    /// The exemption: the DataContext attribute itself names a member of the OUTER type. Checking it
    /// against the re-rooted type would report the very attribute that does the re-rooting.
    /// </summary>
    [Fact]
    public void A_nested_data_context_still_catches_a_binding_the_inner_type_does_not_have()
    {
        var xml = XElement.Parse("""
            <Border xmlns="https://github.com/avaloniaui" DataContext="{Binding Unlocker}">
              <TextBlock IsVisible="{Binding NoSuchMember}" />
            </Border>
            """);

        var unresolved = new List<string>();
        Walk(xml, typeof(MainViewModel), parent: null, unresolved);

        Assert.Contains(unresolved, u => u.Contains("NoSuchMember"));
    }

    [Fact]
    public void A_nested_data_context_naming_a_member_that_does_not_exist_is_itself_reported()
    {
        var xml = XElement.Parse("""
            <Border xmlns="https://github.com/avaloniaui" DataContext="{Binding NotAProperty}">
              <TextBlock IsVisible="{Binding Anything}" />
            </Border>
            """);

        var unresolved = new List<string>();
        Walk(xml, typeof(MainViewModel), parent: null, unresolved);

        Assert.Contains(unresolved, u => u.Contains("NotAProperty"));
    }

    private static IEnumerable<XElement> Ancestry(XElement element)
    {
        for (var e = element.Parent; e is not null; e = e.Parent) yield return e;
    }

    private static void Walk(XElement element, Type context, Type? parent, List<string> unresolved)
    {
        // An `Owner.ItemTemplate` property element re-roots the DataContext at the item type of
        // whatever the owner's ItemsSource binds to: MainViewModel.Banners gives Banner,
        // FilteredRows gives PackRowViewModel. Derived by reflection rather than hard-coded, so
        // a template moved onto a different collection is followed rather than mis-checked.
        // An explicit DataContext="{Binding Prop}" re-roots the subtree at Prop's declared type
        // the same way.
        var declared = element.Attribute("DataContext")?.Value;
        var nested = declared is not null ? BoundMemberType(declared, context) : null;

        var (childContext, childParent) =
            element.Name.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal)
                ? (ItemTypeOf(element.Parent, context, unresolved) ?? context, context)
                : nested is not null
                    ? (nested, context)
                    : (context, parent);

        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.NamespaceName.Length > 0 || attribute.Name.LocalName == "xmlns") continue;

            // The DataContext attribute names a member of the OUTER type, and is what re-roots the
            // rest, so it is the one attribute checked against `context` rather than `childContext`.
            var self = attribute.Name.LocalName == "DataContext" ? context : childContext;
            var selfParent = attribute.Name.LocalName == "DataContext" ? parent : childParent;

            Check(element, attribute, self, selfParent, unresolved);
        }

        foreach (var child in element.Elements()) Walk(child, childContext, childParent, unresolved);
    }

    /// <summary>
    /// The declared type of the member a <c>{Binding Prop}</c> value names on <paramref name="context"/>,
    /// or null when the value names nothing resolvable, in which case the caller leaves the subtree
    /// rooted at the outer context and <see cref="Check"/> reports the attribute itself.
    /// </summary>
    private static Type? BoundMemberType(string value, Type context)
    {
        var path = Path(value);
        if (path is null) return null;

        var member = Member(context, path);

        return member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => null,
        };
    }

    private static Type? ItemTypeOf(XElement? owner, Type context, List<string> unresolved)
    {
        var source = owner?.Attribute("ItemsSource")?.Value;
        if (source is null) return null;

        var name = Path(source);
        if (name is null) return null;

        var member = Member(context, name);

        if (member is null)
        {
            unresolved.Add($"{owner!.Name.LocalName}.ItemsSource: '{name}' is not a public member of {context.Name}");
            return null;
        }

        var type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static void Check(
        XElement element, XAttribute attribute, Type context, Type? parent, List<string> unresolved)
    {
        var value = attribute.Value;
        if (!value.StartsWith("{Binding", StringComparison.Ordinal)) return;

        var path = Path(value);

        // `{Binding}` binds the whole DataContext and names nothing to resolve.
        if (path is null) return;

        // A $parent binding reaches one of two different things, and confusing them is the bug
        // this branch exists to avoid. With `.DataContext.` in the path it walks to an ancestor's
        // VIEW MODEL, which is resolved against `parent`. Without it, it reads a property of the
        // ancestor CONTROL itself - $parent[Window].Bounds.Height - which is a control member and
        // has no bearing on any view model.
        if (path.StartsWith("$parent[", StringComparison.Ordinal)
            && ParentPath(path) is null)
        {
            CheckControlPath(element, attribute, path, unresolved);
            return;
        }

        var (target, targetName) = path.StartsWith("$parent[", StringComparison.Ordinal)
            ? (parent, "the parent DataContext")
            : (context, context.Name);

        Assert.NotNull(target);

        var member = ParentPath(path) ?? path;

        if (Member(target, member) is null)
        {
            unresolved.Add(
                $"{element.Name.LocalName}.{attribute.Name.LocalName}={value}: "
                + $"'{member}' is not a public member of {targetName}");
        }
    }

    /// <summary>
    /// A binding onto an ancestor CONTROL's own property, such as
    /// <c>$parent[Window].Bounds.Height</c>. The control type named in the brackets must exist and
    /// the property chain must resolve on it, so a typo in either fails here rather than binding
    /// to null at runtime, which Avalonia reports nowhere.
    /// </summary>
    private static void CheckControlPath(
        XElement element, XAttribute attribute, string path, List<string> unresolved)
    {
        var match = Regex.Match(path, @"^\$parent\[(?<type>[^\]]+)\]\.(?<chain>[A-Za-z_][A-Za-z0-9_.]*)$");

        if (!match.Success)
        {
            unresolved.Add($"{element.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}: "
                + "this $parent binding is in a form this sweep does not recognise");
            return;
        }

        // Avalonia's own control types, looked up by the name the markup uses. Searched across the
        // Avalonia assemblies rather than hard-coded, so this keeps working for any control.
        var typeName = match.Groups["type"].Value;
        var controlType = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .FirstOrDefault(t => t.Name == typeName && typeof(AvaloniaObject).IsAssignableFrom(t));

        if (controlType is null)
        {
            unresolved.Add($"{element.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}: "
                + $"'{typeName}' names no Avalonia control type");
            return;
        }

        // Every step of the chain, not just the first: Bounds.Height is wrong in two different
        // ways if either half is misspelled.
        var current = controlType;

        foreach (var step in match.Groups["chain"].Value.Split('.'))
        {
            var member = Member(current, step);

            if (member is null)
            {
                unresolved.Add($"{element.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}: "
                    + $"'{step}' is not a public member of {current.Name}");
                return;
            }

            current = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => current,
            };
        }
    }

    /// <summary>
    /// The binding forms this file actually contains: <c>{Binding Name}</c>, the negation
    /// <c>{Binding !Name}</c>, <c>{Binding}</c>, and
    /// <c>{Binding $parent[ItemsControl].DataContext.Name}</c>. A form this does not recognise
    /// fails loudly rather than being skipped: the whole path is looked up as a single member
    /// name, so it lands in the unresolved list and someone has to teach this file about it.
    /// </summary>
    private static string? Path(string value)
    {
        var inner = value[1..^1].Trim();          // strip the braces
        inner = inner["Binding".Length..].Trim(); // and the markup extension's name

        // Bindings in this file carry no comma-separated parameters; if one ever does, only the
        // path is examined.
        var comma = inner.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0) inner = inner[..comma].Trim();

        return inner.Length == 0 ? null : inner.TrimStart('!');
    }

    private static string? ParentPath(string path)
    {
        var match = Regex.Match(path, @"^\$parent\[[^\]]+\]\.DataContext\.(?<member>[A-Za-z_][A-Za-z0-9_]*)$");

        return match.Success ? match.Groups["member"].Value : null;
    }

    private static MemberInfo? Member(Type type, string name) =>
        type.GetMember(name, BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m is PropertyInfo or FieldInfo);

    [Fact]
    public void The_unlocker_region_binds_the_relaunch_command_on_the_region_view_model()
    {
        // Named separately from the sweep for the same reason as the context menu above: this button
        // is the only route to an elevated relaunch, and no view test executes it.
        var region = LoadWindow().Descendants()
            .Single(e => e.Name.LocalName == "Expander"
                         && e.Attribute("DataContext")?.Value == "{Binding Unlocker}");

        Assert.Contains(region.Descendants(),
            e => e.Attribute("Command")?.Value == "{Binding RelaunchElevatedCommand}");

        Assert.NotNull(Member(typeof(UnlockerViewModel), "RelaunchElevatedCommand"));
    }

    [Fact]
    public void The_log_region_binds_the_clear_command_on_the_log_view_model()
    {
        var region = LoadWindow().Descendants()
            .Single(e => e.Name.LocalName == "Border"
                         && e.Attribute("DataContext")?.Value == "{Binding Log}");

        Assert.Contains(region.Descendants(),
                        e => e.Attribute("Command")?.Value == "{Binding ClearCommand}");
        Assert.NotNull(Member(typeof(LogViewModel), "ClearCommand"));
    }

    /// <summary>
    /// Spec §8 rules out a file log, which makes Copy the only way a user can get the log off
    /// their machine to report a bug — pinned the same way ClearCommand is, so a button that
    /// silently loses its binding (the row context menu's original failure mode) is caught here
    /// rather than only in a headless render test.
    /// </summary>
    [Fact]
    public void The_log_region_binds_the_copy_command_on_the_log_view_model()
    {
        var region = LoadWindow().Descendants()
            .Single(e => e.Name.LocalName == "Border"
                         && e.Attribute("DataContext")?.Value == "{Binding Log}");

        Assert.Contains(region.Descendants(),
                        e => e.Attribute("Command")?.Value == "{Binding CopyCommand}");
        Assert.NotNull(Member(typeof(LogViewModel), "CopyCommand"));
    }
}
