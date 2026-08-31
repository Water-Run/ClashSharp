using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerExecutableContractTests
{
    private const string Windows10And11SupportedOsId =
        "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}";

    [Fact]
    public void WpfProjectPinsWindows11X64GreenExecutableSettings()
    {
        XDocument project = XDocument.Load(SourcePath(
            "ClashSharp.Installer",
            "ClashSharp.Installer.csproj"));

        Assert.Equal("net10.0-windows10.0.22000.0", Property(project, "TargetFramework"));
        Assert.Equal("10.0.22000.0", Property(project, "TargetPlatformMinVersion"));
        Assert.Equal("x64", Property(project, "PlatformTarget"));
        Assert.Equal("win-x64", Property(project, "RuntimeIdentifier"));
        Assert.Equal("true", Property(project, "SelfContained"));
        Assert.Equal("true", Property(project, "PublishSingleFile"));
        Assert.Equal("false", Property(project, "PublishTrimmed"));
        Assert.Equal("false", Property(project, "StartupHookSupport"));
        Assert.Equal("true", Property(project, "UseWPF"));
        Assert.Equal("app.manifest", Property(project, "ApplicationManifest"));
        Assert.Equal("ClashSharp.Installer.Program", Property(project, "StartupObject"));
        Assert.Equal(
            "ClashSharp.Installer.ReleaseManifest.json",
            Property(project, "ClashSharpInstallerReleaseManifestLogicalName"));
        Assert.Equal("false", Property(project, "ClashSharpEnableInstallerMutationRuntime"));
        Assert.Contains(project.Descendants("ProjectReference"), reference =>
            string.Equals(
                (string?)reference.Attribute("Include"),
                "..\\ClashSharp.Installer.Windows\\ClashSharp.Installer.Windows.csproj",
                StringComparison.Ordinal));
        XElement embeddedManifest = Assert.Single(
            project.Descendants("EmbeddedResource"),
            static resource =>
                (string?)resource.Attribute("Include") ==
                    "$(ClashSharpInstallerReleaseManifestPath)");
        Assert.Equal(
            "$(ClashSharpInstallerReleaseManifestLogicalName)",
            (string?)embeddedManifest.Attribute("LogicalName"));
        XElement formalTarget = Assert.Single(
            project.Descendants("Target"),
            static target =>
                (string?)target.Attribute("Name") ==
                    "ValidateClashSharpFormalInstallerManifest");
        Assert.Equal(
            "'$(ClashSharpFormalInstallerBuild)' == 'true'",
            (string?)formalTarget.Attribute("Condition"));
        Assert.Equal(2, formalTarget.Elements("Error").Count());
        XElement runtimeTarget = Assert.Single(
            project.Descendants("Target"),
            static target =>
                (string?)target.Attribute("Name") ==
                    "ValidateClashSharpInstallerMutationRuntime");
        Assert.Equal(
            "'$(ClashSharpEnableInstallerMutationRuntime)' == 'true'",
            (string?)runtimeTarget.Attribute("Condition"));
        Assert.Equal(2, runtimeTarget.Elements("Error").Count());
    }

    [Fact]
    public void ExecutableManifestUsesOfficialWindows10And11CompatibilityIdentity()
    {
        XDocument manifest = XDocument.Load(SourcePath("ClashSharp.Installer", "app.manifest"));
        XNamespace compatibility = "urn:schemas-microsoft-com:compatibility.v1";
        XNamespace assemblyV3 = "urn:schemas-microsoft-com:asm.v3";
        XNamespace dpi2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

        XElement supportedOs = Assert.Single(manifest.Descendants(compatibility + "supportedOS"));
        Assert.Equal(Windows10And11SupportedOsId, (string?)supportedOs.Attribute("Id"));

        XElement executionLevel = Assert.Single(
            manifest.Descendants(assemblyV3 + "requestedExecutionLevel"));
        Assert.Equal("asInvoker", (string?)executionLevel.Attribute("level"));
        Assert.Equal("false", (string?)executionLevel.Attribute("uiAccess"));

        XElement dpiAwareness = Assert.Single(manifest.Descendants(dpi2016 + "dpiAwareness"));
        Assert.Equal("PerMonitorV2, PerMonitor", dpiAwareness.Value);
    }

    [Fact]
    public void ShellKeepsOneProductCardAndStateDerivedMaintenanceActions()
    {
        XDocument shell = XDocument.Load(SourcePath("ClashSharp.Installer", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement productCard = Assert.Single(
            shell.Descendants(presentation + "Border"),
            static element =>
                (string?)element.Attribute("AutomationProperties.Name") ==
                    "ClashSharp 产品实例");
        Assert.Same(
            productCard,
            Assert.Single(
                shell.Descendants(presentation + "Border"),
                static element =>
                    (string?)element.Attribute("Style") ==
                        "{StaticResource InstallerCardStyle}"));
        Assert.DoesNotContain(
            shell.Descendants(),
            element => element.Name == presentation + "TabControl"
                || element.Name == presentation + "TabItem"
                || element.Name == presentation + "Frame"
                || element.Name == presentation + "Page"
                || element.Name == presentation + "NavigationWindow");
        XElement productHeading = Assert.Single(
            shell.Descendants(presentation + "TextBlock"),
            static element =>
                (string?)element.Attribute("AutomationProperties.HeadingLevel") ==
                    "Level1");
        Assert.Contains(productHeading, productCard.Descendants());
        Assert.DoesNotContain(
            shell.Descendants(),
            static element =>
                element.Attributes().Any(attribute =>
                    attribute.Value.Contains("ProductGroupTitle", StringComparison.Ordinal)));
        XElement[] buttons = productCard.Descendants(presentation + "Button").ToArray();
        Assert.Equal(3, buttons.Length);

        XElement secondary = Assert.Single(
            buttons,
            static button =>
                (string?)button.Attribute("Command") == "{Binding SecondaryActionCommand}");
        Assert.Equal(
            "{Binding IsSecondaryActionVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)secondary.Attribute("Visibility"));
        XElement cancel = Assert.Single(
            buttons,
            static button =>
                (string?)button.Attribute("Command") == "{Binding CancelCommand}");
        Assert.Equal(
            "{Binding IsCancelActionVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)cancel.Attribute("Visibility"));
        XElement primary = Assert.Single(
            buttons,
            static button =>
                (string?)button.Attribute("Command") == "{Binding PrimaryActionCommand}");
        Assert.Equal(
            "{Binding IsPrimaryActionVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)primary.Attribute("Visibility"));
    }

    [Fact]
    public void CustomEntryPointRoutesTheAuthenticatedHelperAndKeepsTheUiFailClosed()
    {
        string program = File.ReadAllText(SourcePath("ClashSharp.Installer", "Program.cs"));
        string app = File.ReadAllText(SourcePath("ClashSharp.Installer", "App.xaml.cs"));

        Assert.Contains("InstallerStartupRouter.Run(", program, StringComparison.Ordinal);
        Assert.Contains("WindowsInstallerMachineHelper", program, StringComparison.Ordinal);
        Assert.Contains("EmbeddedInstallerReleaseManifest.Load()", program, StringComparison.Ordinal);
        Assert.Contains("MachineHelperFailedExitCode", program, StringComparison.Ordinal);
        Assert.Contains("InvalidMachineHelperArgumentsExitCode", program, StringComparison.Ordinal);
        Assert.DoesNotContain("MachineHelperNotConnectedExitCode", program, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallerMachineHelperInvocation.Parse", program, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallerMachineHelperBootstrap.Parse", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.Exit(0)", program, StringComparison.Ordinal);
        Assert.Contains("CLASHSHARP_INSTALLER_MUTATION_RUNTIME", app, StringComparison.Ordinal);
        Assert.Contains("WindowsProductionInstallerRuntimeFactory.Create", app, StringComparison.Ordinal);
        Assert.Contains("new MigrationPreviewInstallerRuntime()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowCloseWaitsForTheActiveRuntimeGeneration()
    {
        string shell = File.ReadAllText(SourcePath("ClashSharp.Installer", "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(SourcePath(
            "ClashSharp.Installer",
            "MainWindow.xaml.cs"));

        Assert.Contains("Closing=\"OnClosing\"", shell, StringComparison.Ordinal);
        Assert.Contains("Closed=\"OnClosed\"", shell, StringComparison.Ordinal);
        Assert.Contains("_viewModel.IsBusy", codeBehind, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_viewModel.RequestCancellation()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellUsesCSharpPurpleAsItsAccessibleInstallerAccent()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument theme = XDocument.Load(SourcePath(
            "ClashSharp.Installer",
            "Themes",
            "InstallerTheme.xaml"));
        XDocument shell = XDocument.Load(SourcePath("ClashSharp.Installer", "MainWindow.xaml"));

        XElement accent = Assert.Single(
            theme.Descendants(presentation + "Color"),
            color => (string?)color.Attribute(x + "Key") == "InstallerAccentColor");
        Assert.Equal("#7355DD", accent.Value.Trim());
        Assert.Contains(
            theme.Descendants(presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Value") == "True"
                && ((string?)trigger.Attribute("Binding"))?.Contains(
                    "SystemParameters.HighContrast",
                    StringComparison.Ordinal) == true);

        XElement window = Assert.IsType<XElement>(shell.Root);
        Assert.Equal("920", (string?)window.Attribute("Width"));
        Assert.Equal("620", (string?)window.Attribute("Height"));
        Assert.Equal("{StaticResource InstallerWindowStyle}", (string?)window.Attribute("Style"));
        XElement rootGrid = Assert.Single(window.Elements(presentation + "Grid"));
        Assert.Contains(
            rootGrid.Elements(presentation + "Border"),
            border => (string?)border.Attribute("Background") ==
                "{StaticResource InstallerAccentBrush}");

        string themeText = File.ReadAllText(SourcePath(
            "ClashSharp.Installer",
            "Themes",
            "InstallerTheme.xaml"));
        Assert.DoesNotContain("#0C7428", themeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#0B7026", themeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#0A6822", themeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WpfBrandMarkReusesEveryCanonicalMeasuredSvgGeometryLayer()
    {
        XDocument svg = XDocument.Load(SourcePath("ClashSharp", "Assets", "Logo.svg"));
        XNamespace svgNamespace = "http://www.w3.org/2000/svg";
        XElement canonicalHexagon = Assert.Single(
            svg.Descendants(svgNamespace + "path"),
            static path => (string?)path.Attribute("id") == "hexagon");
        Assert.Empty(svg.Descendants(svgNamespace + "text"));
        Assert.Empty(svg.Descendants(svgNamespace + "image"));
        XElement canonicalShadow = Assert.Single(
            svg.Descendants(svgNamespace + "g"),
            static group => (string?)group.Attribute("fill") == "#033E15");
        XElement canonicalMark = Assert.Single(
            svg.Descendants(svgNamespace + "g"),
            static group => (string?)group.Attribute("fill") == "#FFFFFF");
        Assert.Equal("0.16", (string?)canonicalShadow.Attribute("fill-opacity"));
        Assert.Equal(5, canonicalShadow.Elements(svgNamespace + "path").Count());
        Assert.Equal(5, canonicalMark.Elements(svgNamespace + "path").Count());

        XDocument theme = XDocument.Load(SourcePath(
            "ClashSharp.Installer",
            "Themes",
            "InstallerTheme.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement drawing = Assert.Single(
            theme.Descendants(presentation + "DrawingGroup"),
            static group => group.Attribute("ClipGeometry") is not null);

        Assert.Equal(
            NormalizeGeometry((string)canonicalHexagon.Attribute("d")!),
            NormalizeGeometry((string)drawing.Attribute("ClipGeometry")!));

        XElement hexagonDrawing = Assert.Single(
            drawing.Descendants(presentation + "GeometryDrawing"),
            item => item.Element(presentation + "GeometryDrawing.Brush") is not null);
        XElement shadowDrawing = Assert.Single(
            drawing.Descendants(presentation + "GeometryDrawing"),
            static item => (string?)item.Attribute("Brush") == "#29033E15");
        XElement markDrawing = Assert.Single(
            drawing.Descendants(presentation + "GeometryDrawing"),
            static item => (string?)item.Attribute("Brush") == "#FFFFFF");
        Assert.Equal(
            NormalizeGeometry((string)canonicalHexagon.Attribute("d")!),
            NormalizeGeometry((string)hexagonDrawing.Attribute("Geometry")!));
        Assert.Equal(
            NormalizeGeometry(CombinePaths(canonicalShadow, svgNamespace)),
            NormalizeGeometry((string)shadowDrawing.Attribute("Geometry")!));
        Assert.Equal(
            NormalizeGeometry(CombinePaths(canonicalMark, svgNamespace)),
            NormalizeGeometry((string)markDrawing.Attribute("Geometry")!));
    }

    private static string Property(XDocument project, string name) =>
        Assert.Single(project.Descendants(name)).Value;

    private static string NormalizeGeometry(string geometry) =>
        Regex.Replace(geometry.Replace(',', ' '), "\\s+", " ").Trim();

    private static string CombinePaths(XElement group, XNamespace svgNamespace) =>
        string.Join(
            " ",
            group.Elements(svgNamespace + "path")
                .Select(static path => (string)path.Attribute("d")!));

    private static string SourcePath(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClashSharp.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}
