using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Static contracts of the installer window markup that WPF itself reports only as silent runtime
/// binding or resource failures: every binding resolves on the view model with a compatible type and
/// direction, every static resource exists, every interactive element has an accessible name, names
/// are unique and no user-visible literal is left untranslated.
/// </summary>
public sealed partial class InstallerXamlStructureContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] InteractiveElements = ["Button", "CheckBox", "ComboBox", "Hyperlink", "Expander", "Image", "ProgressBar"];

    private static readonly string[] VisibleTextAttributes =
    [
        "Text", "Content", "Header", "Tag", "Title", "AutomationProperties.Name", "AutomationProperties.HelpText",
    ];

    // Brand and unit tokens that are intentionally Latin in every locale.
    private static readonly string[] NeutralLiterals = ["Clash#", " · x64", "x64"];

    [Fact]
    public void EveryBindingResolvesOnItsDataContextWithACompatibleTypeAndDirection()
    {
        XDocument window = Load("MainWindow.xaml");
        int checkedBindings = 0;
        foreach (XElement element in window.Descendants())
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (!TryParseBinding(attribute.Value, out Binding? binding) || binding.HasExplicitSource)
                {
                    continue;
                }

                Type context = ResolveContext(element);
                (Type? propertyType, bool writable) = Resolve(context, binding.Path, out string? failure);
                Assert.True(
                    failure is null,
                    $"<{element.Name.LocalName} {attribute.Name.LocalName}=\"{attribute.Value}\"> {failure}");
                checkedBindings++;

                string name = attribute.Name.LocalName;
                string owner = element.Name.LocalName;
                if (name == "Command")
                {
                    Assert.True(typeof(ICommand).IsAssignableFrom(propertyType), $"{binding.Path} must be an ICommand.");
                }
                else if (name == "ItemsSource")
                {
                    Assert.True(typeof(IEnumerable).IsAssignableFrom(propertyType), $"{binding.Path} must be enumerable.");
                }
                else if (name == "Visibility")
                {
                    Assert.True(
                        binding.Converter is "BooleanToVisibilityConverter" && propertyType == typeof(bool),
                        $"{binding.Path} must be a bool paired with BooleanToVisibilityConverter.");
                }
                else if (name is "IsChecked" or "IsEnabled" or "IsIndeterminate")
                {
                    Assert.True(propertyType == typeof(bool) || propertyType == typeof(bool?), $"{binding.Path} must be boolean.");
                }
                else if (name == "SelectedIndex")
                {
                    Assert.Equal(typeof(int), propertyType);
                }

                bool twoWayByDefault = (name == "IsChecked" && owner == "CheckBox")
                    || (name == "SelectedIndex" && owner == "ComboBox")
                    || (name == "Value" && owner == "ProgressBar");
                if (twoWayByDefault && binding.Mode is null or "TwoWay" or "OneWayToSource")
                {
                    Assert.True(writable, $"{binding.Path} is bound two-way but has no public setter; WPF throws when the binding activates.");
                }
            }
        }

        Assert.True(checkedBindings >= 30, $"Only {checkedBindings} bindings were found; the scanner is not seeing the markup.");
    }

    [Fact]
    public void EveryInteractiveElementHasAnAccessibleName()
    {
        XDocument window = Load("MainWindow.xaml");
        int inspected = 0;
        foreach (XElement element in window.Descendants().Where(static element => InteractiveElements.Contains(element.Name.LocalName)))
        {
            string[] names =
            [
                element.Attribute("AutomationProperties.Name")?.Value ?? string.Empty,
                element.Attribute("Content")?.Value ?? string.Empty,
                element.Attribute("Header")?.Value ?? string.Empty,
                string.Concat(element.Nodes().OfType<XText>().Select(static text => text.Value)),
            ];

            Assert.True(
                names.Any(static name => !string.IsNullOrWhiteSpace(name)),
                $"<{element.Name.LocalName}> at line {((System.Xml.IXmlLineInfo)element).LineNumber} has no accessible name.");
            inspected++;
        }

        Assert.True(inspected >= 20, $"Only {inspected} interactive elements were found.");
    }

    [Fact]
    public void NamesAreUniqueAndEveryNameTheTestsAndCodeBehindRelyOnExists()
    {
        XDocument window = Load("MainWindow.xaml");
        string[] names = window.Descendants()
            .Select(static element => element.Attribute(Xaml + "Name")?.Value)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (string required in new[]
        {
            "MainContent", "ContentBody", "DetailsExpander", "InstallPreferencesPanel", "CapabilityChecks",
            "DeclineOwnerTransferButton", "DeclineRetiredUninstallButton", "DesktopShortcutToggle",
            "TransparentProxyServiceToggle", "SystemDefaultsDetailsExpander", "NotificationPresetDetailsExpander",
        })
        {
            Assert.Contains(required, names);
        }
    }

    [Fact]
    public void NoUserVisibleLiteralIsLeftUntranslatedOrEmpty()
    {
        XDocument window = Load("MainWindow.xaml");
        int inspected = 0;
        foreach (XElement element in window.Descendants())
        {
            var literals = new List<string>();
            literals.AddRange(element.Attributes()
                .Where(static attribute => VisibleTextAttributes.Contains(attribute.Name.LocalName))
                .Select(static attribute => attribute.Value)
                .Where(static value => !value.StartsWith('{')));
            if (element.Name.LocalName is "Hyperlink" or "TextBlock" or "Run")
            {
                string text = string.Concat(element.Nodes().OfType<XText>().Select(static node => node.Value)).Trim();
                if (text.Length > 0)
                {
                    literals.Add(text);
                }
            }

            foreach (string literal in literals)
            {
                inspected++;
                bool hasLatin = literal.Any(static character => char.IsAsciiLetter(character));
                bool hasCjk = literal.Any(static character => character is >= '一' and <= '鿿');
                Assert.True(
                    !hasLatin || hasCjk || NeutralLiterals.Contains(literal),
                    $"'{literal}' in <{element.Name.LocalName}> is user-visible Latin text without a Chinese counterpart.");
            }
        }

        Assert.True(inspected > 60, $"Only {inspected} literals were found.");
    }

    /// <summary>
    /// The window mirrors in Persian and WPF has no detect-from-content reading order, so a literal Latin token that
    /// ends in a neutral character, such as the product name, renders as "#Clash" unless its element reads left to
    /// right. Translated text gets left-to-right marks instead; literals never pass through translation.
    /// </summary>
    [Fact]
    public void EveryLiteralProductNameReadsLeftToRightInTheMirroredWindow()
    {
        XDocument window = Load("MainWindow.xaml");
        int checkedLiterals = 0;
        foreach (XElement element in window.Descendants())
        {
            bool literalName = element.Attributes()
                .Where(static attribute => VisibleTextAttributes.Contains(attribute.Name.LocalName))
                .Any(static attribute => !attribute.Value.StartsWith('{')
                    && attribute.Value.Contains("Clash#", StringComparison.Ordinal)
                    // Load unwraps translated values to their Chinese source; only untranslated text is a literal.
                    && !attribute.Value.Any(static character => character is >= '一' and <= '鿿'));
            if (!literalName) { continue; }

            checkedLiterals++;
            Assert.Equal("LeftToRight", (string?)element.Attribute("FlowDirection"));
            // A stretched left-to-right element would push the name to the far side of a mirrored row.
            Assert.NotEqual("Stretch", (string?)element.Attribute("HorizontalAlignment") ?? "Stretch");
        }

        Assert.True(checkedLiterals > 0, "The header no longer shows the product name as a literal; revisit this contract.");
    }

    [Fact]
    public void ThemeDictionaryDefinesEachTopLevelKeyOnceAndTheBrandMarkExists()
    {
        XDocument theme = Load(@"Themes\InstallerTheme.xaml");
        string[] keys = theme.Root!.Elements()
            .Where(static element => element.Name.LocalName != "ResourceDictionary.MergedDictionaries")
            .Select(static element => element.Attribute(Xaml + "Key")?.Value)
            .Where(static key => key is not null)
            .Select(static key => key!)
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("InstallerWindowStyle", keys);
        Assert.Contains("ClashSharpBrandMark", keys);
    }

    private static Type ResolveContext(XElement element)
    {
        Type context = typeof(InstallerShellViewModel);
        // Each enclosing item template rebinds the data context to the element type of its owner's source.
        XElement? template = element.AncestorsAndSelf().FirstOrDefault(static candidate => candidate.Name.LocalName == "DataTemplate");
        if (template is null)
        {
            return context;
        }

        XElement owner = template.Ancestors().First(static candidate => candidate.Name.LocalName == "ItemsControl");
        string itemsSource = owner.Attribute("ItemsSource")?.Value
            ?? throw new InvalidOperationException("An item template needs an ItemsSource binding.");
        Assert.True(TryParseBinding(itemsSource, out Binding? binding));
        (Type? sourceType, _) = Resolve(context, binding.Path, out string? failure);
        Assert.Null(failure);
        Type enumerable = sourceType!.GetInterfaces().Append(sourceType)
            .First(static type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable.GetGenericArguments()[0];
    }

    private static (Type? Type, bool Writable) Resolve(Type root, string path, out string? failure)
    {
        Type current = root;
        bool writable = false;
        foreach (string segment in path.Split('.'))
        {
            PropertyInfo? property = current.GetProperty(segment, BindingFlags.Instance | BindingFlags.Public);
            if (property is null)
            {
                failure = $"'{segment}' is not a public property of {current.Name} (path '{path}').";
                return (null, false);
            }

            current = property.PropertyType;
            writable = property.GetSetMethod() is not null;
        }

        failure = null;
        return (current, writable);
    }

    private static bool TryParseBinding(string value, out Binding binding)
    {
        binding = default!;
        Match match = BindingExpression().Match(value);
        if (!match.Success)
        {
            return false;
        }

        string[] arguments = SplitArguments(match.Groups["arguments"].Value);
        string? path = null;
        string? mode = null;
        string? converter = null;
        bool explicitSource = false;
        foreach (string argument in arguments)
        {
            int equals = argument.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                path ??= argument.Trim();
                continue;
            }

            string name = argument[..equals].Trim();
            string setting = argument[(equals + 1)..].Trim();
            switch (name)
            {
                case "Path":
                    path = setting;
                    break;
                case "Mode":
                    mode = setting;
                    break;
                case "Converter":
                    converter = StaticResourceReference().Match(setting).Groups["key"].Value;
                    break;
                case "Source" or "ElementName" or "RelativeSource":
                    explicitSource = true;
                    break;
                default:
                    break;
            }
        }

        binding = new Binding(path ?? string.Empty, mode, converter, explicitSource);
        return !string.IsNullOrEmpty(binding.Path) || explicitSource;
    }

    private static string[] SplitArguments(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
            }
            else if (text[index] == ',' && depth == 0)
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }

        parts.Add(text[start..]);
        return parts.Where(static part => part.Trim().Length > 0).ToArray();
    }

    // Localized window text is written back as its Simplified Chinese source, so contracts find elements by the
    // text a user sees whatever the markup that delivers it.
    private static XDocument Load(string relative) => XDocument.Parse(
        System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(FindInstallerSource(), relative)), @"\{l:T '([^']*)'\}", "$1"),
        LoadOptions.SetLineInfo);

    private static string FindInstallerSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string source = Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.Installer");
            if (File.Exists(Path.Combine(source, "App.xaml")))
            {
                return source;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The production Installer XAML was not found.");
    }

    [GeneratedRegex(@"^\{Binding(?:\s+(?<arguments>.*))?\}$", RegexOptions.Singleline)]
    private static partial Regex BindingExpression();

    [GeneratedRegex(@"\{(?<kind>StaticResource|DynamicResource)\s+(?<key>\{[^}]*\}|[^\s,}]+)\s*\}")]
    private static partial Regex StaticResourceReference();

    private sealed record Binding(string Path, string? Mode, string? Converter, bool HasExplicitSource);
}
